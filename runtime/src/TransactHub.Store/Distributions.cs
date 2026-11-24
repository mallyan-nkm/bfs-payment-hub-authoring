using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using Microsoft.EntityFrameworkCore;

namespace TransactHub.Store;

/// <summary>
/// A bank's distributions and configuration releases (docs/runtime/PACKAGING-AND-DEPLOYMENT.md, spec R-09-48 to R-09-57).
/// Both are stored once in the release registry, never changed: a distribution as scope <c>dist/&lt;bank&gt;</c> with its id as
/// the release (<c>2026.11.22-1</c>), a configuration release as scope <c>config/&lt;bank&gt;</c>. The same id with other content is
/// refused. A distribution's state in an environment is an insert-only fact on its own chain
/// (<c>distribution.state</c>: installed, active, draining, drained, removed), with who and why; which one new payments take is
/// the activation row, recorded in the same transaction as the facts of a switch-over. A payment is pinned at acceptance
/// (<c>distribution.pinned</c> on the payment's chain) and ends with a <c>payment.end</c> fact (Issue I-97): a payment is open
/// while it is pinned and has no end.
/// </summary>
public sealed class DistributionRegistry(StoreDbContext db, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public const string DistPrefix = "dist/";
    public const string ConfigPrefix = "config/";
    public const string StateKind = "distribution.state";
    public const string PinKind = "distribution.pinned";
    public const string EndKind = "payment.end";
    public const string ConfigKind = "configuration.installed";
    /// <summary>A configuration release's values, bindings, calendars and tables put in force, once, with the consent it had (R-09-111).</summary>
    public const string InForceKind = "configuration.in-force";
    /// <summary>Installed in no particular environment: a stored distribution is installed everywhere until a state says otherwise.</summary>
    public const string AnyEnvironment = "*";
    public static readonly string[] States = ["installed", "active", "draining", "drained", "removed"];

    public static string Scope(string bank) => DistPrefix + bank;
    public static string IdOf(string bank, string id) => $"{DistPrefix}{bank}@{id}";
    public static string ConfigScope(string bank) => ConfigPrefix + bank;
    public static string ConfigIdOf(string bank, string release) => $"{ConfigPrefix}{bank}@{release}";

    public static (string bank, string id) Split(string distributionId)
    {
        if (!distributionId.StartsWith(DistPrefix, StringComparison.Ordinal) || distributionId.LastIndexOf('@') is var i && i < 0)
            throw new DistributionRefused($"{distributionId} is not a distribution id (dist/<bank>@<id>)");
        return (distributionId[DistPrefix.Length..i], distributionId[(i + 1)..]);
    }

    /// <summary>A distribution's id: a date and a counter, <c>YYYY.MM.DD-n</c>.</summary>
    public static bool IsDistributionId(string id) => System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d{4}\.\d{2}\.\d{2}-[1-9]\d*$");

    /// <summary>Stores a verified distribution. Stored again with the same content changes nothing; other content is refused.</summary>
    public async Task<StoreResult> Store(string bank, string id, byte[] package, JsonObject manifest, string contentHash, string actor, string why, CancellationToken ct = default)
    {
        if (!IsDistributionId(id)) throw new DistributionRefused($"{id} is not a distribution id (YYYY.MM.DD-n)");
        var r = await new ReleaseRegistry(db, clock).Store(Scope(bank), id, package, manifest, actor, ct, contentHash);
        // Installed: when first stored, and again when a removed one is installed again (rollback after removal).
        var dist = IdOf(bank, id);
        var states = await StatesOf(dist, ct);
        if (r.Stored || states.Values.Any(s => s.State == "removed"))
            await Fact(dist, "installed", AnyEnvironment, actor, why, new JsonObject { ["contentHash"] = contentHash }, ct);
        return r;
    }

    /// <summary>Stores a verified configuration release; same release and same content changes nothing, other content is refused.</summary>
    public Task<StoreResult> StoreConfiguration(string bank, string release, byte[] package, JsonObject manifest, string contentHash, string actor, CancellationToken ct = default) =>
        new ReleaseRegistry(db, clock).Store(ConfigScope(bank), release, package, manifest, actor, ct, contentHash);

    Task<FactRow> Fact(string dist, string state, string environment, string actor, string? why, JsonObject? extra, CancellationToken ct)
    {
        var data = extra ?? new JsonObject();
        data["environment"] = environment;
        if (why is not null) data["why"] = why;
        return new FactLog(db, clock).Append(dist, "distribution", new NewFact(StateKind, state, data, dist, actor), ct);
    }

    public sealed record StateFact(string State, DateTimeOffset At, string By, string? Why);

    /// <summary>The last state fact of a distribution per environment ("*" is the installation).</summary>
    public async Task<Dictionary<string, StateFact>> StatesOf(string dist, CancellationToken ct = default)
    {
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == dist && f.Kind == StateKind).OrderBy(f => f.Seq).ToListAsync(ct);
        var d = new Dictionary<string, StateFact>(StringComparer.Ordinal);
        foreach (var f in facts)
        {
            var data = JsonNode.Parse(f.DataJson);
            var env = (string?)data?["environment"] ?? AnyEnvironment;
            var s = new StateFact(f.Code ?? "", f.At, f.Actor, (string?)data?["why"]);
            if (env == AnyEnvironment) { foreach (var k in d.Keys.ToList()) if (d[k].State == "removed") d[k] = s; }
            d[env] = s;
        }
        return d;
    }

    /// <summary>A distribution's state in an environment: its last state there, else installed when stored.</summary>
    public async Task<string?> StateIn(string dist, string environment, CancellationToken ct = default)
    {
        var s = await StatesOf(dist, ct);
        if (s.TryGetValue(environment, out var e) && (!s.TryGetValue(AnyEnvironment, out var any) || any.At <= e.At)) return e.State;
        return s.ContainsKey(AnyEnvironment) ? "installed" : null;
    }

    public async Task<List<string>> Of(string bank, CancellationToken ct = default) =>
        (await db.Releases.AsNoTracking().Where(r => r.Scope == Scope(bank)).Select(r => r.Release).ToListAsync(ct)).Order(StringComparer.Ordinal).Select(r => IdOf(bank, r)).ToList();

    public async Task<List<string>> Banks(CancellationToken ct = default) =>
        (await db.Releases.AsNoTracking().Where(r => r.Scope.StartsWith(DistPrefix)).Select(r => r.Scope).Distinct().ToListAsync(ct)).Select(s => s[DistPrefix.Length..]).Order(StringComparer.Ordinal).ToList();

    /// <summary>The distribution new payments of a bank take in an environment now: the one whose state there is active.</summary>
    public async Task<string?> Active(string bank, string environment, CancellationToken ct = default)
    {
        foreach (var d in await Of(bank, ct))
            if (await StateIn(d, environment, ct) == "active") return d;
        return null;
    }

    /// <summary>
    /// Activation, the switch-over: the distribution becomes active and the one active until now draining, in one transaction,
    /// with the activation row. A removed distribution must be installed again first; one already active changes nothing.
    /// </summary>
    public async Task<(string active, string? draining)> Activate(string dist, string environment, string actor, string why, CancellationToken ct = default)
    {
        var (bank, id) = Split(dist);
        if (await new ReleaseRegistry(db, clock).Get(Scope(bank), id, ct) is null) throw new DistributionRefused($"{dist} is not installed; install it first");
        var state = await StateIn(dist, environment, ct);
        if (state == "removed") throw new DistributionRefused($"{dist} was removed from {environment}; install it again from GitHub before activating it");
        // The last activation's number first, then the active distribution (R-09-137): a writer that read the number before another
        // activation committed takes the same number and is refused by the store's unique key, so it never drains a distribution
        // that is no longer the active one.
        var sequence = await ReleaseRegistry.LastSequence(db, environment, Scope(bank), ct) + 1;
        var previous = await Active(bank, environment, ct);
        if (previous == dist) return (dist, null);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Activations.Add(new ActivationRow
        {
            Environment = environment, Scope = Scope(bank), Release = id, EffectiveFrom = StoreTime.Of(clock.GetUtcNow()), Actor = actor, RecordedAt = StoreTime.Of(clock.GetUtcNow()),
            Sequence = sequence,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            throw new ActivationRaced($"Another activation of {bank} in {environment} was recorded first, as activation {sequence}: {dist} was not activated. Read the active distribution and activate again if it is still wanted", e);
        }
        await Fact(dist, "active", environment, actor, why, previous is null ? new JsonObject { ["activation"] = sequence } : new JsonObject { ["replaces"] = previous, ["activation"] = sequence }, ct);
        if (previous is not null) await Fact(previous, "draining", environment, actor, why, new JsonObject { ["replacedBy"] = dist, ["activation"] = sequence }, ct);
        await tx.CommitAsync(ct);
        return (dist, previous);
    }

    /// <summary>Draining by hand: the distribution takes no new payment; its open payments run to their end.</summary>
    public async Task Drain(string dist, string environment, string actor, string why, CancellationToken ct = default)
    {
        var state = await StateIn(dist, environment, ct);
        if (state != "active") throw new DistributionRefused($"{dist} is {state ?? "not installed"} in {environment}: only an active distribution drains");
        await Fact(dist, "draining", environment, actor, why, null, ct);
    }

    /// <summary>Rollback: the distribution that was active before the current one is activated again. A removed one must be installed again first.</summary>
    public async Task<(string active, string? draining)> Rollback(string bank, string environment, string actor, string why, CancellationToken ct = default)
    {
        var current = await Active(bank, environment, ct) ?? throw new DistributionRefused($"No distribution of {bank} is active in {environment}");
        var acts = await db.Activations.AsNoTracking().Where(a => a.Environment == environment && a.Scope == Scope(bank)).OrderByDescending(a => a.Id).Select(a => a.Release).ToListAsync(ct);
        var (_, currentId) = Split(current);
        var previous = acts.FirstOrDefault(r => r != currentId) ?? throw new DistributionRefused($"{bank} has no earlier distribution in {environment} to roll back to");
        return await Activate(IdOf(bank, previous), environment, actor, $"rollback: {why}", ct);
    }

    /// <summary>
    /// Pins a payment at acceptance to the distribution active for its bank and environment then. The pin never changes: a
    /// payment pinned already keeps its distribution. No active distribution: the payment is refused.
    /// </summary>
    public async Task<string> Pin(string paymentId, string bank, string environment, string actor, CancellationToken ct = default)
    {
        if (await PinOf(paymentId, ct) is { } pinned) return pinned;
        var dist = await Active(bank, environment, ct) ?? throw new DistributionRefused($"No distribution of {bank} is active in {environment}: the payment is not accepted");
        await new FactLog(db, clock).Append(paymentId, "payment", new NewFact(PinKind, dist, new JsonObject { ["bank"] = bank, ["environment"] = environment }, dist, actor), ct);
        return dist;
    }

    public Task<string?> PinOf(string paymentId, CancellationToken ct = default) =>
        db.Facts.AsNoTracking().Where(f => f.SubjectId == paymentId && f.Kind == PinKind).OrderBy(f => f.Seq).Select(f => f.Code).FirstOrDefaultAsync(ct);

    /// <summary>A payment's end (Issue I-97): its root instance ended with this outcome. Recorded once.</summary>
    public async Task<bool> End(string paymentId, string outcome, string actor, CancellationToken ct = default)
    {
        var dist = await PinOf(paymentId, ct) ?? throw new DistributionRefused($"{paymentId} is not pinned to a distribution");
        if (await db.Facts.AsNoTracking().AnyAsync(f => f.SubjectId == paymentId && f.Kind == EndKind, ct)) return false;
        await new FactLog(db, clock).Append(paymentId, "payment", new NewFact(EndKind, outcome, new JsonObject { ["outcome"] = outcome }, dist, actor), ct);
        return true;
    }

    /// <summary>The outcome of a payment pinned but never started (Issue I-206, R-09-164).</summary>
    public const string Abandoned = "abandoned";

    /// <summary>
    /// A payment pinned but never started ends <c>abandoned</c>, with its reason (Issue I-206, R-09-164): the front door's start failed
    /// after the pin (no route, no worker, the engine refused), or a bank approver ended a stuck pin. Recorded once; a payment that
    /// has an end keeps it.
    /// </summary>
    public async Task<bool> Abandon(string paymentId, string reason, string actor, string? code = null, CancellationToken ct = default)
    {
        var dist = await PinOf(paymentId, ct) ?? throw new DistributionRefused($"{paymentId} is not pinned to a distribution");
        if (await db.Facts.AsNoTracking().AnyAsync(f => f.SubjectId == paymentId && f.Kind == EndKind, ct)) return false;
        var data = new JsonObject { ["outcome"] = Abandoned, ["reason"] = reason };
        if (code is not null) data["code"] = code;
        await new FactLog(db, clock).Append(paymentId, "payment", new NewFact(EndKind, Abandoned, data, dist, actor), ct);
        return true;
    }

    /// <summary>A pin with no payment: pinned at least as long ago as asked, with no end, and no run of any engine accepted it.</summary>
    public sealed record StuckPin(string PaymentId, string Distribution, string Bank, string Environment, DateTimeOffset PinnedAt, string? LastFact);

    /// <summary>
    /// The pins with no payment (Issue I-206, R-09-165): pinned at least <paramref name="olderThan"/> ago, with no end, and with no
    /// payload an engine's run created for the payment (the claim check's <c>create</c> fact names the payment's id when a run
    /// accepts it). Such a pin counts as an open payment of its distribution until it is ended.
    /// </summary>
    public async Task<List<StuckPin>> Unstarted(TimeSpan olderThan, CancellationToken ct = default)
    {
        var before = clock.GetUtcNow() - olderThan;
        var pins = await db.Facts.AsNoTracking().Where(f => f.Kind == PinKind).Select(f => new { f.SubjectId, f.Code, f.At, f.DataJson }).ToListAsync(ct);
        var ended = (await db.Facts.AsNoTracking().Where(f => f.Kind == EndKind).Select(f => f.SubjectId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var stuck = new List<StuckPin>();
        foreach (var p in pins.Where(p => !ended.Contains(p.SubjectId) && p.At <= before).OrderBy(p => p.At))
        {
            var id = p.SubjectId;
            // It started when an engine's run took it: its payload was created or accepted for it, or an engine wrote on its own chain
            // (a bulk's steps, its instance's ids). A salary file's run on Temporal does only the latter two (Issue I-339).
            if (await db.Facts.AsNoTracking().AnyAsync(f => f.SubjectKind == "payload" && (f.Code == "create" || f.Code == "accepted") && f.DataJson.Contains(id), ct)) continue;
            if (await db.Facts.AsNoTracking().AnyAsync(f => f.SubjectId == id && (f.Kind == "step" || f.Kind == "engine"), ct)) continue;
            var last = await db.Facts.AsNoTracking().Where(f => f.SubjectId == id).OrderByDescending(f => f.Seq).Select(f => f.Kind).FirstOrDefaultAsync(ct);
            var d = JsonNode.Parse(p.DataJson);
            stuck.Add(new StuckPin(id, p.Code!, (string?)d?["bank"] ?? "", (string?)d?["environment"] ?? "", p.At, last));
        }
        return stuck;
    }

    /// <summary>The open payments per distribution: pinned, with no end.</summary>
    public async Task<Dictionary<string, int>> OpenPayments(CancellationToken ct = default)
    {
        var pins = await db.Facts.AsNoTracking().Where(f => f.Kind == PinKind).Select(f => new { f.SubjectId, f.Code }).ToListAsync(ct);
        var ended = (await db.Facts.AsNoTracking().Where(f => f.Kind == EndKind).Select(f => f.SubjectId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        return pins.Where(p => !ended.Contains(p.SubjectId)).GroupBy(p => p.Code!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }

    /// <summary>When the last of a distribution's payments ended, if any did.</summary>
    public async Task<DateTimeOffset?> LastEnd(string dist, CancellationToken ct = default)
    {
        var ends = await db.Facts.AsNoTracking().Where(f => f.Kind == EndKind && f.ReleaseId == dist).Select(f => f.At).ToListAsync(ct);
        return ends.Count == 0 ? null : ends.Max();
    }

    /// <summary>The longest wait a distribution declares (its manifest's <c>longestWait</c>), zero when it declares none.</summary>
    public async Task<TimeSpan> LongestWait(string dist, CancellationToken ct = default)
    {
        var (bank, id) = Split(dist);
        var row = await new ReleaseRegistry(db, clock).Get(Scope(bank), id, ct) ?? throw new DistributionRefused($"{dist} is not installed");
        return Duration((string?)JsonNode.Parse(row.ManifestJson)?["longestWait"]) ?? TimeSpan.Zero;
    }

    /// <summary>A draining distribution with no open payment is drained: recorded once, when it is first seen so.</summary>
    public async Task<string?> Refresh(string dist, string environment, string actor, CancellationToken ct = default)
    {
        var state = await StateIn(dist, environment, ct);
        if (state == "draining" && (await OpenPayments(ct)).GetValueOrDefault(dist) == 0)
        {
            await Fact(dist, "drained", environment, actor, "no open payment", null, ct);
            return "drained";
        }
        return state;
    }

    /// <summary>
    /// Removal: refused while the distribution is active or installed and never drained, while any payment pinned to it is open,
    /// and before its longest declared wait has passed since it stopped taking payments and since its last payment ended.
    /// The package stays in the store and on GitHub as a record.
    /// </summary>
    public async Task Remove(string dist, string environment, string actor, string why, CancellationToken ct = default)
    {
        var state = await Refresh(dist, environment, actor, ct);
        if (state is null) throw new DistributionRefused($"{dist} is not installed");
        if (state == "removed") return;
        if (state == "active") throw new DistributionRefused($"{dist} is active in {environment}: activate another distribution first");
        var open = (await OpenPayments(ct)).GetValueOrDefault(dist);
        if (open > 0) throw new DistributionRefused($"{dist} has {open} open payment(s) pinned to it: it may be removed once they end");
        var wait = await LongestWait(dist, ct);
        var now = clock.GetUtcNow();
        var s = await StatesOf(dist, ct);
        var stopped = s.TryGetValue(environment, out var e) && e.State is "draining" or "drained"
            ? (await db.Facts.AsNoTracking().Where(f => f.SubjectId == dist && f.Kind == StateKind && f.Code == "draining").Select(f => f.At).ToListAsync(ct)).DefaultIfEmpty(e.At).Max()
            : (DateTimeOffset?)null;
        var last = await LastEnd(dist, ct);
        foreach (var since in new[] { stopped, last })
            if (since is { } t && t + wait > now)
                throw new DistributionRefused($"{dist} may be removed from {(t + wait).ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}: its longest declared wait ({XmlConvert.ToString(wait)}) has not passed since {(since == stopped ? "it stopped taking payments" : "its last payment ended")}");
        await Fact(dist, "removed", environment, actor, why, new JsonObject { ["open"] = 0 }, ct);
    }

    /// <summary>An ISO 8601 duration (P30D, PT1H), or null.</summary>
    public static TimeSpan? Duration(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        try { return XmlConvert.ToTimeSpan(iso); } catch (FormatException) { return null; }
    }
}

/// <summary>
/// The parameter contract of a distribution: every dial it declares, as <c>&lt;artefact&gt;#&lt;parameter&gt;</c>, with its type and bounds
/// (min and max for numbers and durations, allowed and pattern for codes and strings, each item of a list), as R-04-08 and the
/// validator's L14 read them.
/// </summary>
public static class ParameterContract
{
    /// <summary>The dials of a contract (the manifest's <c>parameterContract</c>).</summary>
    public static Dictionary<string, JsonObject> Of(JsonNode? contract)
    {
        var d = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var p in contract?["parameters"] as JsonArray ?? [])
            if (p is JsonObject o && (string?)o["parameter"] is { } name) d[name] = o;
        return d;
    }

    /// <summary>Why a value does not fit a dial, or null when it does.</summary>
    public static string? Misfit(JsonObject decl, JsonNode? value)
    {
        var type = (string?)decl["type"];
        var b = decl["bounds"] as JsonObject ?? new JsonObject();
        if (type == "list")
        {
            if (value is not JsonArray a) return "is not a list";
            foreach (var x in a) if (Item(b, "item", x) is { } why) return why;
            return null;
        }
        if (value is JsonObject map)
        {
            foreach (var (_, x) in map) if (Item(b, "item", x) is { } why) return why;
            return null;
        }
        return Item(b, type, value);
    }

    static decimal? Num(JsonNode? n) => n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    static string? Item(JsonObject b, string? type, JsonNode? value)
    {
        if (type == "duration")
        {
            var v = value is JsonValue jv && jv.TryGetValue<string>(out var s) ? DistributionRegistry.Duration(s) : null;
            if (v is null) return "is not an ISO 8601 duration";
            if (b["min"] is { } mn && DistributionRegistry.Duration((string?)mn) is { } min && v < min) return $"is below its minimum {(string?)mn}";
            if (b["max"] is { } mx && DistributionRegistry.Duration((string?)mx) is { } max && v > max) return $"is above its maximum {(string?)mx}";
        }
        else if (Num(value) is { } num)
        {
            if (Num(b["min"]) is { } min && num < min) return $"is below its minimum {min.ToString(CultureInfo.InvariantCulture)}";
            if (Num(b["max"]) is { } max && num > max) return $"is above its maximum {max.ToString(CultureInfo.InvariantCulture)}";
        }
        if (b["allowed"] is JsonArray allowed && !allowed.Any(a => JsonNode.DeepEquals(a, value))) return "is not one of the allowed values";
        if (b["pattern"] is JsonValue pat && value is JsonValue sv && sv.TryGetValue<string>(out var str) && !System.Text.RegularExpressions.Regex.IsMatch(str, (string)pat!)) return $"does not match {(string?)pat}";
        return null;
    }

    /// <summary>
    /// A configuration release's values against the contracts of the distributions that will read them: a value no distribution
    /// declares is unknown; a value outside the bounds of any distribution that declares it does not fit. A distribution that
    /// does not declare a dial ignores its value.
    /// </summary>
    public static List<string> Check(IEnumerable<(string parameter, JsonNode? value)> values, IReadOnlyDictionary<string, JsonNode?> contracts)
    {
        var problems = new List<string>();
        var parsed = contracts.ToDictionary(c => c.Key, c => Of(c.Value));
        foreach (var (parameter, value) in values)
        {
            var declaring = parsed.Where(c => c.Value.ContainsKey(parameter)).ToList();
            if (declaring.Count == 0) { problems.Add($"{parameter} is not a dial of any installed distribution ({string.Join(", ", contracts.Keys.Order(StringComparer.Ordinal))})"); continue; }
            foreach (var (dist, dials) in declaring)
                if (Misfit(dials[parameter], value) is { } why) problems.Add($"{parameter} = {value?.ToJsonString()} {why} in {dist}");
        }
        return problems;
    }
}

/// <summary>An activation another activation of the same bank and environment beat to the store (R-09-137): nothing was changed.</summary>
public sealed class ActivationRaced(string message, Exception inner) : InvalidOperationException(message, inner)
{
    /// <summary>The error dictionary's code.</summary>
    public string Code => "distribution.ActivationRaced";
}

public sealed class DistributionRefused(string message) : InvalidOperationException(message)
{
    /// <summary>The error dictionary's code (Issue I-103).</summary>
    public string Code => "service.RemovalRefused";
}
