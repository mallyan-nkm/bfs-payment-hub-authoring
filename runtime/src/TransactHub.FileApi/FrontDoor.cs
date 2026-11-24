using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;
using YamlDotNet.RepresentationModel;

namespace TransactHub.FileApi;

// The runtime's front door (R-09-85; docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 5; owner's decision of 23 November 2026). It
// runs in the File API's process, behind its intake: every file that reaches the runtime passes the intake's checks and is kept
// once, and then the front door decides what it is by the scheme's dispatch table in the distribution active for the bank:
//
//   start    a message that starts a payment (the PIF, the SIF, the RTC...): the payment is pinned to the active distribution
//            (R-09-53), the scheme's intake map runs on the worker of its queue, the dispatch table's route stands after it
//            (R-05-42), p.table.workflow-select.v1 chooses the platform workflow and the leg fills its slot (R-05-08); the engine
//            host is asked to start it, and the payment's correlation keys are recorded as facts.
//   deliver  a message for a running payment (an ACK or NAK, the PSI, an answer): found by its correlation keys among the
//            payments the front door started, it is handed to the engine host of that payment's distribution, never another.
//
// The same routing is the studio deploy runner's (studio/src/deploy/route.ts): one fixture of routes holds both to it
// (runtime/tests/TransactHub.FileApi.Tests/front-door-routes.json).

/// <summary>What a scheme's dispatch table says of one message.</summary>
public sealed record MessageEntry(string Id, string Scheme, string Direction, JsonObject Disposition, JsonObject? Message = null)
{
    public JsonObject? Start => Disposition["start"] as JsonObject;
    public JsonObject? Deliver => Disposition["deliver"] as JsonObject;
    /// <summary>A scheme's report, reconciled on arrival (R-09-210, Issue I-164): its map, its activity and the period it covers.</summary>
    public JsonObject? Reconcile => Disposition["reconcile"] as JsonObject;
    /// <summary>The rules of the message's file the scheme declares (R-09-209, Issue I-159), or null.</summary>
    public JsonObject? File => Message?["file"] as JsonObject;
    /// <summary>The deliver disposition's correlation map (R-09-212, Issue I-267), or null.</summary>
    public string? CorrelationMap => (string?)Deliver?["correlationMap"];
    public string Kind => Start is not null ? "start" : Deliver is not null ? "deliver" : Reconcile is not null ? "reconcile" : Disposition["sentBy"] is not null ? "sent" : "other";
}

/// <summary>Where a start message goes: the header's route, the platform workflow, and the legs in its slots.</summary>
public sealed record StartRoute(string Message, string Scheme, string IntakeMap, JsonObject Route, string Workflow, IReadOnlyDictionary<string, string> Legs, string InboundDirection);

/// <summary>A refusal of the front door: the message, the file, or the distribution does not allow it.</summary>
public sealed class FrontDoorRefused(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The dispatch tables and the documents the front door reads, from one distribution (or any set of documents): the scheme
/// manifests, the legs (for their slot) and <c>p.table.workflow-select.v1</c>. Each document is the latest release in the set.
/// </summary>
public sealed class Dispatch
{
    readonly Dictionary<string, JsonObject> byName = new(StringComparer.Ordinal);
    readonly List<JsonObject> schemes = new();
    public const string WorkflowSelect = "p.table.workflow-select.v1";

    public static Dispatch Of(IEnumerable<JsonObject> documents)
    {
        var d = new Dispatch();
        foreach (var doc in documents)
        {
            var kind = Str(doc, "kind");
            if (kind == "scheme")
            {
                var same = d.schemes.FindIndex(s => Str(s, "scope") == Str(doc, "scope"));
                if (same < 0) d.schemes.Add(doc);
                else if (CompareReleases(Str(doc, "release"), Str(d.schemes[same], "release")) > 0) d.schemes[same] = doc;
                continue;
            }
            if (Str(doc, "name") is { Length: > 0 } n && (!d.byName.TryGetValue(n, out var have) || CompareReleases(Str(doc, "release"), Str(have, "release")) > 0))
                d.byName[n] = doc;
        }
        return d;
    }

    /// <summary>Every YAML document of a package (a distribution's zip, or an export package), as JSON.</summary>
    public static IEnumerable<JsonObject> DocumentsOfZip(byte[] zip)
    {
        using var a = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        foreach (var e in a.Entries.Where(e => e.FullName.EndsWith(".yaml", StringComparison.Ordinal)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var r = new StreamReader(e.Open(), Encoding.UTF8);
            if (Yaml(r.ReadToEnd()) is JsonObject o) yield return o;
        }
    }

    /// <summary>Every YAML document under a folder (the content branch's <c>artefacts/</c>), as JSON.</summary>
    public static IEnumerable<JsonObject> DocumentsOfFolder(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            if (Yaml(File.ReadAllText(f)) is JsonObject o) yield return o;
    }

    public IReadOnlyList<JsonObject> Schemes => schemes;
    public JsonObject? Document(string name) => byName.GetValueOrDefault(name);

    /// <summary>The dispatch table's entry of a message, or null when no scheme of the distribution names it.</summary>
    public MessageEntry? Entry(string message)
    {
        foreach (var s in schemes)
            foreach (var m in (s["messages"] as JsonArray ?? []).OfType<JsonObject>())
                if (Str(m, "id") == message)
                    return new MessageEntry(message, Str(s, "scope"), Str(m, "direction"), m["disposition"] as JsonObject ?? new JsonObject(), m);
        return null;
    }

    /// <summary>
    /// The route of a start message, as the deploy runner's front door works it out: the dispatch table's route on the header
    /// (kind, direction, segment), the platform workflow p.table.workflow-select.v1 chooses for it, and the leg (and refuse leg) in
    /// their slots.
    /// </summary>
    public StartRoute Route(string message, DateOnly evaluationDate)
    {
        var e = Entry(message) ?? throw new FrontDoorRefused("frontdoor.UnknownMessage", $"No scheme of the distribution names the message {message}");
        if (e.Start is not { } start) throw new FrontDoorRefused("frontdoor.NotAStart", $"{message} does not start a payment: its disposition is {e.Kind}");
        var map = Str(start, "intakeMap");
        if (map.Length == 0) throw new FrontDoorRefused("frontdoor.NoIntakeMap", $"{message} names no intake map");
        var route = new JsonObject();
        foreach (var k in new[] { "kind", "direction", "segment" }) if (Str(start, k) is { Length: > 0 } v) route[k] = v;
        var workflow = Select(route, evaluationDate) ?? throw new FrontDoorRefused("frontdoor.NoWorkflow", $"{WorkflowSelect} chooses no workflow for {message}");
        var legName = Str(start, "leg");
        var leg = Document(legName) ?? throw new FrontDoorRefused("frontdoor.NoLeg", $"The leg {legName} that {message} starts is not in the distribution");
        var legs = new SortedDictionary<string, string>(StringComparer.Ordinal) { [Str(leg["implements"] as JsonObject, "slot") is { Length: > 0 } slot ? slot : "scheme-leg"] = legName };
        if (Str(start, "refuseLeg") is { Length: > 0 } refuse) legs["scheme-refuse"] = refuse;
        return new StartRoute(message, e.Scheme, map, route, workflow, legs, e.Direction == "in" ? "inward" : "outward");
    }

    /// <summary>
    /// p.table.workflow-select.v1 on a header. The front door's table is a decision table of literal cells: a row matches when each
    /// of its <c>when</c> cells equals the header's input; the hit policy is unique. A cell that is not a literal, a row valid on
    /// another date or a second match is refused, so the front door never guesses a workflow.
    /// </summary>
    public string? Select(JsonObject header, DateOnly on)
    {
        var t = Document(WorkflowSelect) ?? throw new FrontDoorRefused("frontdoor.NoWorkflowSelect", $"The distribution has no {WorkflowSelect}");
        if (Str(t, "hitPolicy") is { Length: > 0 } hp && hp != "unique") throw new FrontDoorRefused("frontdoor.TableNotSupported", $"{WorkflowSelect} has hit policy {hp}; the front door reads unique tables");
        var inputs = (t["inputs"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(i => Str(i, "name"), i => Str(i, "expression"), StringComparer.Ordinal);
        string? Input(string name)
        {
            var expr = inputs.GetValueOrDefault(name) ?? throw new FrontDoorRefused("frontdoor.TableNotSupported", $"{WorkflowSelect} has no input {name}");
            if (!expr.StartsWith("header.", StringComparison.Ordinal) || expr.Count(c => c == '.') != 1) throw new FrontDoorRefused("frontdoor.TableNotSupported", $"{WorkflowSelect} reads {expr}; the front door reads header fields");
            return Str(header, expr["header.".Length..]) is { Length: > 0 } v ? v : null;
        }
        var hits = new List<string>();
        foreach (var row in (t["rows"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Str(row, "validFrom") is { Length: > 0 } vf && DateOnly.Parse(vf[..10], CultureInfo.InvariantCulture) > on) continue;
            if (Str(row, "validTo") is { Length: > 0 } vt && DateOnly.Parse(vt[..10], CultureInfo.InvariantCulture) < on) continue;
            var when = row["when"] as JsonObject ?? new JsonObject();
            var match = true;
            foreach (var (k, v) in when)
            {
                if (v is not JsonValue jv || jv.GetValueKind() != System.Text.Json.JsonValueKind.String) throw new FrontDoorRefused("frontdoor.TableNotSupported", $"{WorkflowSelect}: the cell {k} is not a literal");
                if (Input(k) != jv.GetValue<string>()) { match = false; break; }
            }
            if (match && Str(row["then"] as JsonObject, "workflow") is { Length: > 0 } wf) hits.Add(wf);
        }
        return hits.Count switch
        {
            0 => null,
            1 => hits[0],
            _ => throw new FrontDoorRefused("frontdoor.TableNotUnique", $"{WorkflowSelect} matches {hits.Count} rows for {header.ToJsonString()}"),
        };
    }

    public static string Str(JsonObject? o, string k) => o?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public static int CompareReleases(string a, string b)
    {
        static int[] P(string r) => r.Split('.').Select(x => int.TryParse(x, out var i) ? i : 0).ToArray();
        var (x, y) = (P(a), P(b));
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    public static JsonNode? Yaml(string text)
    {
        var ys = new YamlStream();
        ys.Load(new StringReader(text));
        return ys.Documents.Count == 0 ? null : Json(ys.Documents[0].RootNode);
    }

    static JsonNode? Json(YamlNode n) => n switch
    {
        YamlSequenceNode s => new JsonArray(s.Children.Select(Json).ToArray()),
        YamlMappingNode m => new JsonObject(m.Children.Select(kv => KeyValuePair.Create(((YamlScalarNode)kv.Key).Value!, Json(kv.Value)))),
        YamlScalarNode { Style: not YamlDotNet.Core.ScalarStyle.Plain } q => JsonValue.Create(q.Value),
        YamlScalarNode p when p.Value is "true" or "false" => JsonValue.Create(p.Value == "true"),
        YamlScalarNode p when p.Value is null or "~" or "null" => null,
        YamlScalarNode p => JsonValue.Create(p.Value),
        _ => null,
    };
}

/// <summary>Runs an intake map: on the worker of the map's queue, as the deploy runner does (POST /attempt).</summary>
public interface IIntakeRunner
{
    /// <summary>Runs the map on the inbound message; answers its outcome and its writes.</summary>
    Task<(string Outcome, JsonObject Writes)> Map(string distribution, string intakeMap, JsonObject context, JsonObject message, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>The engine host of a bank (its supervisor): starts a pinned payment, and hands a message to a running one.</summary>
public interface IEngineHost
{
    Task Start(string paymentId, string distribution, StartRoute route, JsonObject context, CancellationToken ct = default);
    Task<string> Deliver(string paymentId, string distribution, JsonObject message, CancellationToken ct = default);
}

/// <summary>A file the intake has kept, as the front door is given it.</summary>
public sealed record FrontDoorFile(string Tenant, string Environment, string Message, string FileName, byte[] Content, string FileId, string Sha256,
    IReadOnlyDictionary<string, string>? Correlation = null);

/// <summary>What the front door did with a file.</summary>
public sealed record FrontDoorResult(string Disposition, string? PaymentId, string? Distribution, string? Workflow, string Detail);

/// <summary>The runtime's front door (R-09-85): start a payment, or correlate a message to a running one.</summary>
public sealed class RuntimeFrontDoor(StoreOptions store, IIntakeRunner intake, IEngineHost engines, TimeProvider? clock = null)
{
    public const string CorrelationKind = "frontdoor.correlation";
    public const string StartedKind = "frontdoor.started";
    public const string DeliveredKind = "frontdoor.delivered";
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>The correlation keys a front door records, and the keys a message is matched by.</summary>
    public static readonly string[] Keys = ["conversation", "paymentId", "endToEndId", "fileId", "fileStem"];

    /// <summary>A file name without its extensions (<c>000123.PIF.gpg</c> is <c>000123</c>): how a supplement names the file it pairs with, when it gives no key.</summary>
    public static string Stem(string fileName)
    {
        var n = Path.GetFileName(fileName);
        var i = n.IndexOf('.');
        return i > 0 ? n[..i] : n;
    }

    /// <summary>A key as the store keeps it: <c>&lt;key&gt;:&lt;value&gt;</c>, hashed when it would be longer than a fact's code.</summary>
    public static string KeyCode(string tenant, string key, string value)
    {
        var s = $"{tenant}|{key}:{value}";
        return s.Length <= 64 ? s : $"{key}#{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..(62 - key.Length)]}";
    }

    /// <summary>The documents of the distribution active for the bank, from the store (the release registry keeps its package).</summary>
    public async Task<(string Distribution, Dispatch Dispatch)> Active(string bank, string environment, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var reg = new DistributionRegistry(db, clock);
        var dist = await reg.Active(bank, environment, ct) ?? throw new FrontDoorRefused("frontdoor.NoActiveDistribution", $"No distribution of {bank} is active in {environment}");
        return (dist, await DispatchOf(db, dist, ct));
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dispatch> cache = new(StringComparer.Ordinal);

    static async Task<Dispatch> DispatchOf(StoreDbContext db, string dist, CancellationToken ct)
    {
        if (cache.TryGetValue(dist, out var d)) return d;
        var (bank, id) = DistributionRegistry.Split(dist);
        var row = await new ReleaseRegistry(db).Get(DistributionRegistry.Scope(bank), id, ct) ?? throw new FrontDoorRefused("frontdoor.NoPackage", $"{dist} is not stored");
        // A distribution holds an engine package per workflow, each with its own copy of the documents: the latest of each counts.
        return cache[dist] = Dispatch.Of(Dispatch.DocumentsOfZip(row.Package));
    }

    /// <summary>What a message is, by the active distribution's dispatch table, before anything is done with it.</summary>
    public async Task<MessageEntry> Classify(string bank, string environment, string message, CancellationToken ct = default)
    {
        var (_, d) = await Active(bank, environment, ct);
        return d.Entry(message) ?? throw new FrontDoorRefused("frontdoor.UnknownMessage", $"No scheme of the active distribution of {bank} names the message {message}");
    }

    /// <summary>The context and inbound message a map of the front door runs on: a received file, its name and content.</summary>
    static (JsonObject ctx, JsonObject msg) FileView(MessageEntry entry, string fileName, byte[] content)
    {
        var msg = new JsonObject { ["id"] = $"in-{Guid.NewGuid():N}"[..20], ["type"] = entry.Id, ["fileName"] = fileName, ["payload"] = Encoding.UTF8.GetString(content) };
        var ctx = new JsonObject
        {
            ["header"] = new JsonObject { ["scheme"] = entry.Scheme },
            ["messages"] = new JsonObject { ["inbound"] = new JsonObject { ["id"] = msg["id"]!.DeepClone(), ["type"] = entry.Id, ["fileName"] = fileName } },
        };
        return (ctx, msg);
    }

    /// <summary>
    /// The keys a received file of a deliver message gives by its scheme's correlation map (R-09-212, Issue I-267, I-160 (2)): the map
    /// runs on the worker of the active distribution, on the file's name and content, and answers <c>messages.correlation</c>. Null when
    /// the disposition names no map; refused when the map reads no key, so the file is never guessed onto a payment.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>?> CorrelationKeys(string bank, string environment, MessageEntry entry, string fileName, byte[] content, CancellationToken ct = default)
    {
        if (entry.CorrelationMap is not { Length: > 0 } map) return null;
        var (dist, _) = await Active(bank, environment, ct);
        var (ctx, msg) = FileView(entry, fileName, content);
        var (outcome, writes) = await intake.Map(dist, map, ctx, msg, clock.GetUtcNow(), ct);
        foreach (var (p, v) in writes) SetPath(ctx, p, v?.DeepClone());
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in ctx["messages"]?["correlation"] as JsonObject ?? new JsonObject())
            if (v is JsonValue && v.ToString() is { Length: > 0 } s) keys[k] = s;
        if (keys.Count == 0) throw new FrontDoorRefused("frontdoor.Uncorrelated", $"The correlation map {map} read no key of the payment this {entry.Id} answers (it answered {outcome})");
        return keys;
    }

    /// <summary>
    /// The lines of a scheme's report as its reconcile map reads them from the scheme's layout (R-09-210, Issue I-164): the map answers
    /// <c>messages.report.lines</c>, each <c>{ reference, amount, currency, status }</c>.
    /// </summary>
    public async Task<List<ReportLine>> ReportLines(string bank, string environment, MessageEntry entry, string fileName, byte[] content, CancellationToken ct = default)
    {
        var map = (string?)entry.Reconcile?["map"] ?? throw new FrontDoorRefused("frontdoor.NotAReport", $"{entry.Id} is not a report");
        var (dist, _) = await Active(bank, environment, ct);
        var (ctx, msg) = FileView(entry, fileName, content);
        var (outcome, writes) = await intake.Map(dist, map, ctx, msg, clock.GetUtcNow(), ct);
        if (outcome != "mapped") throw new FrontDoorRefused("frontdoor.ReportUnreadable", $"The report map {map} answered {outcome}");
        foreach (var (p, v) in writes) SetPath(ctx, p, v?.DeepClone());
        var lines = new List<ReportLine>();
        foreach (var l in (ctx["messages"]?["report"]?["lines"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var n = lines.Count + 1;
            var reference = (string?)l["reference"] ?? throw new FrontDoorRefused("frontdoor.ReportUnreadable", $"Line {n} of the report names no reference");
            decimal? amount = l["amount"] is JsonNode a && decimal.TryParse(a.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
            lines.Add(new ReportLine(n, reference, amount, (string?)l["currency"], (string?)l["status"]));
        }
        return lines;
    }

    /// <summary>
    /// The running payment a message is for: the payment whose recorded keys match one of the message's (its own, else the stem of its
    /// file name). Null when none matches; refused when two open payments do.
    /// </summary>
    public async Task<string?> Correlate(string tenant, IReadOnlyDictionary<string, string> keys, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var codes = keys.Where(k => Keys.Contains(k.Key) && k.Value.Length > 0).Select(k => KeyCode(tenant, k.Key, k.Value)).ToList();
        if (codes.Count == 0) return null;
        var found = await db.Facts.AsNoTracking().Where(f => f.Kind == CorrelationKind && f.Code != null && codes.Contains(f.Code)).Select(f => f.SubjectId).Distinct().ToListAsync(ct);
        if (found.Count <= 1) return found.SingleOrDefault();
        var ended = await db.Facts.AsNoTracking().Where(f => found.Contains(f.SubjectId) && f.Kind == DistributionRegistry.EndKind).Select(f => f.SubjectId).ToListAsync(ct);
        var open = found.Except(ended).ToList();
        return open.Count switch
        {
            1 => open[0],
            0 => found.Order(StringComparer.Ordinal).Last(),
            _ => throw new FrontDoorRefused("frontdoor.Ambiguous", $"The message matches {open.Count} open payments"),
        };
    }

    /// <summary>A new payment's id: the File API names it before the file is kept, so the file is the payment's from the start.</summary>
    public static string NewPaymentId(string scheme) => $"{scheme.Replace('.', '-')}-{Guid.NewGuid():N}"[..Math.Min(64, scheme.Length + 33)];

    /// <summary>
    /// Starts a payment from a kept file: pins it, runs the intake map, routes it, records its correlation keys and asks the engine
    /// host to run it. A map that does not answer <c>mapped</c> is refused, and nothing starts.
    /// </summary>
    public async Task<FrontDoorResult> Start(FrontDoorFile f, string paymentId, CancellationToken ct = default)
    {
        var (_, dispatch) = await Active(f.Tenant, f.Environment, ct);
        var now = clock.GetUtcNow();
        var route = dispatch.Route(f.Message, DateOnly.FromDateTime(now.UtcDateTime));
        string dist;
        await using (var db = StoreDbContext.Open(store))
        {
            if (route.Scheme is { Length: > 0 } scheme && await IntakeSwitch.Paused(db, f.Tenant, scheme, ct))
                throw new FrontDoorRefused("ops.IntakePaused", new IntakePaused(f.Tenant, scheme).Message);
            dist = await new DistributionRegistry(db, clock).Pin(paymentId, f.Tenant, f.Environment, "front-door", ct);
        }
        // Pinned, and not yet a payment: a start that fails from here on ends the pin abandoned, with its reason, so the pin does not
        // keep its distribution open (Issue I-206, R-09-164).
        try
        {
            var conversation = $"conv-{paymentId}";
            var header = new JsonObject
            {
                ["conversation"] = conversation, ["paymentId"] = paymentId, ["scheme"] = route.Scheme, ["direction"] = route.InboundDirection,
                ["receivedAt"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            };
            foreach (var (k, v) in route.Route) header[k] = v?.DeepClone();
            var msg = new JsonObject
            {
                ["id"] = $"in-{f.FileId}", ["type"] = f.Message, ["fileName"] = f.FileName, ["fileId"] = f.FileId, ["sha256"] = f.Sha256,
                ["payload"] = Encoding.UTF8.GetString(f.Content),
            };
            var ctx = new JsonObject { ["header"] = header, ["messages"] = new JsonObject { ["inbound"] = new JsonObject { ["id"] = msg["id"]!.DeepClone(), ["type"] = f.Message } } };
            var (outcome, writes) = await intake.Map(dist, route.IntakeMap, ctx, msg, now, ct);
            if (outcome != "mapped") throw new FrontDoorRefused("frontdoor.IntakeRefused", $"The intake map {route.IntakeMap} answered {outcome}");
            foreach (var (p, v) in writes) SetPath(ctx, p, v?.DeepClone());
            // The dispatch table routes, not the intake map (R-05-42): its values stand after the map.
            var h = (JsonObject)ctx["header"]!;
            foreach (var (k, v) in route.Route) h[k] = v?.DeepClone();
            h["scheme"] = route.Scheme;
            h["paymentId"] = paymentId;
            var keys = new Dictionary<string, string>(StringComparer.Ordinal) { ["conversation"] = conversation, ["paymentId"] = paymentId, ["fileStem"] = Stem(f.FileName) };
            foreach (var k in new[] { "endToEndId", "fileId" }) if (Dispatch.Str(h, k) is { Length: > 0 } v) keys[k] = v;
            await using (var db = StoreDbContext.Open(store))
            {
                var log = new FactLog(db, clock);
                await log.AppendMany(paymentId, "payment", [
                    .. keys.Select(k => new NewFact(CorrelationKind, KeyCode(f.Tenant, k.Key, k.Value), new JsonObject { ["key"] = k.Key, ["value"] = k.Value }, dist, "front-door")),
                    new NewFact(StartedKind, route.Workflow, new JsonObject { ["message"] = f.Message, ["file"] = f.FileId, ["legs"] = new JsonObject(route.Legs.Select(l => KeyValuePair.Create(l.Key, (JsonNode?)l.Value))) }, dist, "front-door"),
                ], ct);
            }
            await engines.Start(paymentId, dist, route, ctx, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await AbandonAfterFailedStart(paymentId, e, ct);
            throw;
        }
        return new FrontDoorResult("started", paymentId, dist, route.Workflow, $"{route.Workflow} with {string.Join(", ", route.Legs.Values)}");
    }

    /// <summary>The front door's start failed after the pin: the pin ends abandoned with the failure's code and words (R-09-164).</summary>
    async Task AbandonAfterFailedStart(string paymentId, Exception e, CancellationToken ct)
    {
        var code = e is FrontDoorRefused r ? r.Code : "frontdoor.StartFailed";
        try
        {
            await using var db = StoreDbContext.Open(store);
            await new DistributionRegistry(db, clock).Abandon(paymentId, e.Message, "front-door", code, CancellationToken.None);
        }
        catch (Exception x) when (x is not OperationCanceledException) { Console.Error.WriteLine($"{paymentId}: the abandoned start was not recorded: {x.Message}"); }
    }

    /// <summary>Hands a message for a running payment to the engine host of the distribution the payment is pinned to.</summary>
    public async Task<FrontDoorResult> Deliver(FrontDoorFile f, string paymentId, CancellationToken ct = default)
    {
        string dist;
        await using (var db = StoreDbContext.Open(store))
            dist = await new DistributionRegistry(db, clock).PinOf(paymentId, ct) ?? throw new FrontDoorRefused("frontdoor.NotPinned", $"{paymentId} is not pinned to a distribution");
        var msg = new JsonObject
        {
            ["id"] = $"in-{f.FileId}", ["type"] = f.Message, ["conversation"] = $"conv-{paymentId}", ["fileName"] = f.FileName, ["fileId"] = f.FileId,
            ["payload"] = Encoding.UTF8.GetString(f.Content),
            ["correlation"] = new JsonObject((f.Correlation ?? new Dictionary<string, string>()).Select(k => KeyValuePair.Create(k.Key, (JsonNode?)k.Value))),
        };
        var answer = await engines.Deliver(paymentId, dist, msg, ct);
        await using (var db = StoreDbContext.Open(store))
            await new FactLog(db, clock).Append(paymentId, "payment", new NewFact(DeliveredKind, f.Message, new JsonObject { ["file"] = f.FileId, ["answer"] = answer }, dist, "front-door"), ct);
        return new FrontDoorResult("delivered", paymentId, dist, null, answer);
    }

    static void SetPath(JsonObject root, string path, JsonNode? value)
    {
        var parts = path.Replace("items[]", "items", StringComparison.Ordinal).Split('.');
        var cur = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (cur[parts[i]] is not JsonObject next) cur[parts[i]] = next = new JsonObject();
            cur = next;
        }
        var last = parts[^1];
        // A write to a whole part merges into what the front door set (header.kind, scheme, paymentId), as the deploy runner does.
        if (cur[last] is JsonObject have && value is JsonObject add)
            foreach (var (k, v) in add) have[k] = v?.DeepClone();
        else cur[last] = value;
    }
}

/// <summary>The intake map on a worker of the distribution: <c>POST {worker}/attempt</c>, the worker of the map's queue by its scope.</summary>
public sealed class WorkerIntake(Func<string, string, string?> workerOf, HttpClient? http = null) : IIntakeRunner
{
    readonly HttpClient http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

    public static string ScopeOf(string name)
    {
        var p = name.Split('.');
        return p[0] is "p" or "b" ? p[0] : string.Join('.', p.Take(2));
    }

    public async Task<(string Outcome, JsonObject Writes)> Map(string distribution, string intakeMap, JsonObject context, JsonObject message, DateTimeOffset now, CancellationToken ct = default)
    {
        var url = workerOf(distribution, ScopeOf(intakeMap)) ?? throw new FrontDoorRefused("frontdoor.NoWorker", $"No worker of {distribution} serves {ScopeOf(intakeMap)} for {intakeMap}");
        var body = new JsonObject
        {
            ["instance"] = "intake", ["activity"] = intakeMap, ["step"] = "intake", ["call"] = 1, ["attempt"] = 1, ["now"] = now.ToUnixTimeMilliseconds(),
            ["ctx"] = context.DeepClone(), ["messages"] = new JsonArray(message.DeepClone()), ["tenant"] = Dispatch.Str(context["header"] as JsonObject, "tenant"),
            // The distribution the payment is pinned to: a native intake map runs that distribution's build (R-09-130).
            ["distribution"] = distribution,
        };
        using var res = await http.PostAsync($"{url.TrimEnd('/')}/attempt", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var r = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject ?? throw new FrontDoorRefused("frontdoor.IntakeRefused", $"The worker of {intakeMap} answered {(int)res.StatusCode}");
        if (Dispatch.Str(r, "kind") != "result") throw new FrontDoorRefused("frontdoor.IntakeRefused", $"Intake {intakeMap} failed: {Dispatch.Str(r, "message")}");
        return (Dispatch.Str(r, "outcome"), r["writes"] as JsonObject ?? new JsonObject());
    }
}

/// <summary>
/// The engine host's supervisor of the bank (runtime/tools/TransactHub.EngineHost): <c>POST /payments</c> starts a pinned payment on the host of
/// its distribution, <c>POST /payments/{id}/messages</c> hands it a message.
/// </summary>
public sealed class HttpEngineHost(Func<string, string?> supervisorOf, string? token = null, HttpClient? http = null) : IEngineHost
{
    /// <summary>The engine host did not answer (R-09-244, Issue I-353): a 503, with no exception's text.</summary>
    public const string EngineUnreachable = "frontdoor.EngineUnreachable";
    readonly HttpClient http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

    async Task<JsonObject> Post(string url, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (token is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage res;
        // An engine host that does not answer is a refusal with the dictionary's code, never the exception's text, which names the
        // runtime's hosts (R-09-244, Issue I-353). The file is kept; the channel sends it again, or an operator resends it.
        try { res = await http.SendAsync(req, ct); }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new FrontDoorRefused(EngineUnreachable, "The engine host does not answer now: the file is kept, and no payment started; send it again later");
        }
        using var _res = res;
        var text = await res.Content.ReadAsStringAsync(ct);
        var o = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        if (!res.IsSuccessStatusCode) throw new FrontDoorRefused("frontdoor.EngineRefused", $"The engine host answered {(int)res.StatusCode}: {Dispatch.Str(o, "error")}");
        return o;
    }

    string Url(string distribution) => supervisorOf(DistributionRegistry.Split(distribution).bank)?.TrimEnd('/')
        ?? throw new FrontDoorRefused("frontdoor.NoEngineHost", $"No engine host is configured for {DistributionRegistry.Split(distribution).bank}");

    public Task Start(string paymentId, string distribution, StartRoute route, JsonObject context, CancellationToken ct = default) =>
        Post($"{Url(distribution)}/payments", new JsonObject
        {
            ["paymentId"] = paymentId, ["context"] = context.DeepClone(), ["workflow"] = route.Workflow,
            ["legs"] = new JsonObject(route.Legs.Select(l => KeyValuePair.Create(l.Key, (JsonNode?)l.Value))),
        }, ct);

    public async Task<string> Deliver(string paymentId, string distribution, JsonObject message, CancellationToken ct = default) =>
        Dispatch.Str(await Post($"{Url(distribution)}/payments/{Uri.EscapeDataString(paymentId)}/messages", new JsonObject { ["message"] = message.DeepClone() }, ct), "answer") is { Length: > 0 } a ? a : "delivered";
}
