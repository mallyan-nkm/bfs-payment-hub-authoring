using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Service;

/// <summary>A read-only tool the assistant may call: its name, what it does, and its input schema (JSON Schema properties).</summary>
public sealed record AssistantTool(string Name, string Description, JsonObject Properties, string[] Required);
public sealed record ToolCall(string Id, string Name, JsonObject Input);
public sealed record ToolOutcome(string Id, string Content, bool IsError);
public sealed record ModelReply(string Text, IReadOnlyList<ToolCall> Calls, string StopReason, string? Model);

/// <summary>The model behind the assistant: the Anthropic API in a host, a fake in the tests. Only masked text reaches it.</summary>
public interface IAssistantModel
{
    string Name { get; }
    IAssistantConversation Start(string system, IReadOnlyList<AssistantTool> tools);
}

/// <summary>One conversation: the model's history (thinking blocks included) stays inside it.</summary>
public interface IAssistantConversation
{
    Task<ModelReply> Ask(string userText, CancellationToken ct);
    Task<ModelReply> Answer(IReadOnlyList<ToolOutcome> results, CancellationToken ct);
}

/// <summary>
/// What is masked before anything leaves the runtime (spec R-09-44): the telemetry's patterns (IBANs, emails, long numbers)
/// and every value of the parties the assistant may see: names, accounts, IBANs, and the extension fields that name a
/// person (employee and person ids), as the store holds them. A name has no pattern, so it is replaced by its value.
/// </summary>
public sealed class AssistantMasker
{
    readonly List<string> values = new();
    public const string Token = "[masked]";

    public void Add(string? v)
    {
        if (v is { Length: >= 3 } && !values.Contains(v)) values.Add(v);
    }

    // A person's free text in a fact (an operator's remarks, a service engineer's reason) can name anyone: it is never sent.
    static readonly Regex FreeText = new("\"(why|remarks|reason|operatorName|by)\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.CultureInvariant);

    public string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        text = FreeText.Replace(text, m => $"\"{m.Groups[1].Value}\":\"{Token}\"");
        foreach (var v in values.OrderByDescending(x => x.Length)) text = text.Replace(v, Token, StringComparison.OrdinalIgnoreCase);
        return Masked.Text(text);
    }

    static readonly Regex PersonalKey = new("(name|iban|account|person|employee|employer|passport|emirates|phone|email|cif|nationalid|birth|address)", RegexOptions.IgnoreCase);

    static void Walk(JsonNode? n, string key, AssistantMasker m)
    {
        switch (n)
        {
            case JsonObject o: foreach (var (k, v) in o) Walk(v, k, m); break;
            case JsonArray a: foreach (var x in a) Walk(x, key, m); break;
            case JsonValue v when PersonalKey.IsMatch(key): m.Add(v.ToString()); break;
        }
    }

    /// <summary>Learns the party values of every payment and bulk the store holds for these tenants (or all, for <c>*</c>).</summary>
    public static async Task<AssistantMasker> ForStore(StoreOptions store, ServiceIdentity who, CancellationToken ct)
    {
        var m = new AssistantMasker();
        await using var db = StoreDbContext.Open(store);
        var all = who.Tenants.Contains(ServiceIdentity.AnyTenant);
        var tenants = who.Tenants.ToList();
        foreach (var p in await db.Parties.AsNoTracking().Where(x => all || tenants.Contains(x.Tenant)).Select(x => new { x.Name, x.Lei, x.AnyBic, x.OtherIdsJson, x.StreetName, x.TownName }).Take(20000).ToListAsync(ct))
        {
            m.Add(p.Name); m.Add(p.StreetName); Walk(JsonNode.Parse(p.OtherIdsJson), "id", m);
        }
        foreach (var p in await db.Payments.AsNoTracking().Where(x => all || tenants.Contains(x.Tenant)).Select(x => new { a = x.CreditorAccount.Iban, b = x.CreditorAccount.OtherId, c = x.DebtorAccount.Iban, d = x.DebtorAccount.OtherId, e = x.CreditorAccount.Name, f = x.DebtorAccount.Name, x.SupplementaryJson }).Take(50000).ToListAsync(ct))
        {
            m.Add(p.a); m.Add(p.b); m.Add(p.c); m.Add(p.d); m.Add(p.e); m.Add(p.f); Walk(JsonNode.Parse(p.SupplementaryJson), "", m);
        }
        foreach (var b in await db.Bulks.AsNoTracking().Where(x => all || tenants.Contains(x.Tenant)).Select(x => new { a = x.DebtorAccount.Iban, b = x.DebtorAccount.OtherId, c = x.DebtorAccount.Name, x.SupplementaryJson }).Take(20000).ToListAsync(ct))
        {
            m.Add(b.a); m.Add(b.b); m.Add(b.c); Walk(JsonNode.Parse(b.SupplementaryJson), "", m);
        }
        return m;
    }
}

public sealed record AssistantAsk(string Question, string? PaymentId, IReadOnlyList<AssistantTurn>? History);
public sealed record AssistantTurn(string Question, string Answer);

/// <summary>
/// The service console's troubleshooting assistant (spec R-09-44). It is grounded in the north-star documents, the error
/// dictionary and its runbooks, the installed workflows and their versions, and the selected payment's trace, facts and
/// masked logs; it uses read-only tools only and cites its sources; it may propose a troubleshooting action with the runbook
/// it relies on, which a person confirms in the console. It never executes one: no tool it has changes anything.
/// </summary>
public sealed class AssistantService(IAssistantModel? model, bool enabled, StoreOptions store, HealthMonitor health, EventLog log, ContentService content, ErrorDictionary dictionary, string? docsRoot)
{
    public const string ModelId = "claude-opus-5-5";
    const int MaxRounds = 8;

    public bool Enabled => enabled && model is not null;

    public static readonly AssistantTool[] Tools =
    [
        new("get_health", "The runtime's health now: each engine host and server, the store, the read model and its lag, each port's probe, the workers and their versions, the signing keys and certificates, disk. Source: health.", new JsonObject(), []),
        new("get_payment_trace", "A payment's or bulk's facts on its chain (steps, holds, operator and service actions), its legs and status, masked. Source: trace:<id>.", Obj(("paymentId", "string", "The payment or bulk id")), ["paymentId"]),
        new("recent_errors", "Recent errors and events from the store's facts and the host's logs, masked, optionally for one error code, tenant or scheme. Source: logs.", Obj(("code", "string", "An error code"), ("tenant", "string", "A tenant id"), ("scheme", "string", "A scheme scope, e.g. uae.wps")), []),
        new("get_configuration", "The installed releases (workflow packages) with their state (active, draining, drained, installed, removed), open payments, activations and signers. Source: configuration.", new JsonObject(), []),
        new("lookup_error", "The error dictionary's entry for a code: meaning, causes, retryable, ISO reason code, and its runbook (checks, safe actions, the console's troubleshooting actions, escalation). Source: error:<code>.", Obj(("code", "string", "The error code as raised"), ("scheme", "string", "The payment's scheme, for its own entries")), ["code"]),
        new("search_docs", "Search the runtime's design documents (docs/north-star): returns the passages that match, with their file. Source: doc:<path>.", Obj(("query", "string", "Words to look for")), ["query"]),
        new("propose_action", "Propose a troubleshooting action for a person to confirm in the service console. It does not run the action. Give the runbook (the error code) it relies on. Source: proposal.", Obj(("action", "string", "One of the service console's troubleshooting actions"), ("target", "string", "The payment, release, job or key it acts on"), ("runbook", "string", "The error code whose runbook names the action"), ("why", "string", "Why, in one sentence")), ["action", "runbook", "why"]),
    ];

    static JsonObject Obj(params (string name, string type, string description)[] ps) =>
        new(ps.Select(p => KeyValuePair.Create(p.name, (JsonNode?)new JsonObject { ["type"] = p.type, ["description"] = p.description })));

    public string System()
    {
        var docs = docsRoot is not null && Directory.Exists(docsRoot)
            ? string.Join("\n", Directory.EnumerateFiles(docsRoot, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(f => $"- doc:{Path.GetRelativePath(docsRoot, f).Replace('\\', '/')}"))
            : "(no documents)";
        var codes = string.Join("\n", dictionary.All.Select(e => $"- error:{e.Code} ({e.Scope}, {e.Category}{(e.Retryable ? ", retryable" : "")}): {e.Title}"));
        var actions = string.Join("\n", ActionBook.Catalogue.Select(a => $"- {a.Id}: {a.Title}{(a.Risky ? " (needs a bank approver's consent)" : "")}{(a.Path == "missing" ? " (no engine path yet: recorded and refused)" : "")}"));
        return string.Join("\n", [
            "You are the troubleshooting assistant of the TransactHub runtime's service console. You help a bank's service engineer find why a payment or a component is in trouble and what to do.",
            "Write British English, short sentences, plain words, no em-dashes.",
            "Use the tools to look before you answer. Every tool is read-only. You never change anything: to suggest a troubleshooting action, call propose_action with the runbook it relies on; a person confirms it in the console.",
            "Cite every fact you state with its source in square brackets, as the tools give it: [health], [trace:<id>], [logs], [configuration], [error:<code>], [doc:<path>]. Do not state what no source says; say what you would need instead.",
            "Personal data is masked before you see it ([masked], ****1234). Never ask for it and never try to reconstruct it.",
            "Scheme facts of uae.wps are provisional: say so when you rely on one.",
            "<documents>", docs, "</documents>",
            "<error-dictionary>", codes, "</error-dictionary>",
            "<troubleshooting-actions>", actions, "</troubleshooting-actions>",
        ]);
    }

    public async Task<JsonObject> Ask(ServiceIdentity who, AssistantAsk ask, CancellationToken ct = default)
    {
        if (!Enabled) throw new ServiceRefused(503, ErrorCodes.AssistantDisabled, "The assistant is off (TH_ASSISTANT_ENABLED, TH_ANTHROPIC_API_KEY)");
        if (string.IsNullOrWhiteSpace(ask.Question)) throw new ServiceRefused(400, ErrorCodes.RemarksRequired, "Ask a question");
        var masker = await AssistantMasker.ForStore(store, who, ct);
        var sources = new List<string>();
        var proposals = new JsonArray();
        var context = new List<string> { "<context>" };
        var states = await content.States(ct);
        context.Add("Installed releases: " + (states.Count == 0 ? "none" : string.Join("; ", states.Select(s => $"{s.Key} ({s.Value})"))) + " [configuration]");
        sources.Add("configuration");
        if (ask.PaymentId is { Length: > 0 } pid)
        {
            var trace = await log.Trace(who, pid, ct);
            context.Add(trace is null ? $"The selected payment {pid} is not visible to this engineer." : $"The selected payment's trace [trace:{pid}]:\n{trace.ToJsonString()}");
            if (trace is not null) sources.Add($"trace:{pid}");
        }
        context.Add("</context>");
        foreach (var t in ask.History ?? []) context.Insert(0, $"Earlier question: {t.Question}\nEarlier answer: {t.Answer}");
        var conversation = model!.Start(masker.Mask(System()), Tools);
        var reply = await conversation.Ask(masker.Mask(string.Join("\n", context) + "\n\nQuestion: " + ask.Question), ct);
        for (var round = 0; reply.Calls.Count > 0 && round < MaxRounds; round++)
        {
            var results = new List<ToolOutcome>();
            foreach (var c in reply.Calls)
            {
                try
                {
                    var (text, source) = await Run(who, c, proposals, ct);
                    if (!sources.Contains(source)) sources.Add(source);
                    results.Add(new ToolOutcome(c.Id, masker.Mask(text), false));
                }
                catch (Exception e) when (e is ServiceRefused or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    results.Add(new ToolOutcome(c.Id, masker.Mask(e.Message), true));
                }
            }
            reply = await conversation.Answer(results, ct);
        }
        var cited = Regex.Matches(reply.Text, @"\[((?:health|logs|configuration|proposal)|(?:trace|error|doc):[^\]\s]+)\]").Select(m => m.Groups[1].Value).Distinct().ToList();
        return new JsonObject
        {
            ["answer"] = reply.Text, ["sources"] = new JsonArray(sources.Union(cited).Distinct().Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["cited"] = new JsonArray(cited.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["proposals"] = proposals, ["model"] = reply.Model ?? model.Name, ["stopReason"] = reply.StopReason,
            ["note"] = "Proposals are not carried out: confirm one on the Actions page.",
        };
    }

    async Task<(string text, string source)> Run(ServiceIdentity who, ToolCall c, JsonArray proposals, CancellationToken ct)
    {
        // Every input is checked against its tool's schema before the tool runs, whichever model sent it.
        var tool = Tools.FirstOrDefault(t => t.Name == c.Name) ?? throw new KeyNotFoundException($"No tool {c.Name}");
        if (c.Input.ContainsKey("__invalid") || !tool.Required.All(c.Input.ContainsKey) || c.Input.Any(kv => !tool.Properties.ContainsKey(kv.Key) || kv.Value is not JsonValue))
            throw new InvalidOperationException($"INVALID_JSON: the input of {c.Name} is not valid for its schema; send it again");
        string? S(string k) => c.Input.TryGetPropertyValue(k, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        switch (c.Name)
        {
            case "get_health":
                var h = health.Latest().Count == 0 ? await health.Sample(ct) : health.Latest();
                return (new JsonArray(h.Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["group"] = r.Group, ["status"] = r.Status, ["detail"] = r.Detail, ["code"] = r.Code, ["version"] = r.Version, ["at"] = r.At.ToString("O") }).ToArray()).ToJsonString(), "health");
            case "get_payment_trace":
                var id = S("paymentId") ?? throw new KeyNotFoundException("paymentId is needed");
                var t = await log.Trace(who, id, ct) ?? throw new KeyNotFoundException($"No payment {id} for your tenants");
                return (t.ToJsonString(), $"trace:{id}");
            case "recent_errors":
                var r = await log.Query(who, new LogFilter(S("code"), null, S("scheme"), S("tenant"), null, null, null, null, 50), ct);
                var errs = new JsonArray(r["items"]!.AsArray().OfType<JsonObject>().Where(i => (string?)i["level"] == "Error").Take(30).Select(i => i.DeepClone()).ToArray());
                return (errs.ToJsonString(), "logs");
            case "get_configuration":
                return ((await content.Releases(ct)).ToJsonString(), "configuration");
            case "lookup_error":
                var code = S("code") ?? throw new KeyNotFoundException("code is needed");
                var e = dictionary.Find(code, S("scheme")) ?? throw new KeyNotFoundException($"No dictionary entry for {code}");
                var doc = (JsonObject)e.Doc.DeepClone();
                doc["scope"] = e.Scope;
                doc["dictionary"] = $"{e.Dictionary}@{e.Release}";
                return (doc.ToJsonString(), $"error:{e.Code}");
            case "search_docs":
                var q = S("query") ?? throw new KeyNotFoundException("query is needed");
                return SearchDocs(q);
            case "propose_action":
                var action = S("action") ?? throw new KeyNotFoundException("action is needed");
                var spec = ActionBook.Spec(action);
                var runbook = S("runbook") ?? "";
                var entry = dictionary.Find(runbook);
                if (entry is null) throw new KeyNotFoundException($"No runbook {runbook}: propose an action with the error code whose runbook names it");
                var p = new JsonObject
                {
                    ["action"] = action, ["title"] = spec.Title, ["target"] = S("target"), ["runbook"] = entry.Code, ["runbookLink"] = entry.Link, ["why"] = S("why"),
                    ["risky"] = spec.Risky, ["path"] = spec.Path, ["inRunbook"] = entry.Actions.Contains(action), ["confirm"] = $"#/service/actions?action={Uri.EscapeDataString(action)}&target={Uri.EscapeDataString(S("target") ?? "")}",
                };
                proposals.Add(p);
                return ($"Proposed {action} for a person to confirm in the console. It has not been carried out.", "proposal");
            default:
                throw new KeyNotFoundException($"No tool {c.Name}");
        }
    }

    (string, string) SearchDocs(string query)
    {
        if (docsRoot is null || !Directory.Exists(docsRoot)) return ("No documents are available on this host.", "doc:none");
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToArray();
        var hits = new List<(int score, string path, string text)>();
        foreach (var f in Directory.EnumerateFiles(docsRoot, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(docsRoot, f).Replace('\\', '/');
            foreach (var para in File.ReadAllText(f).Split("\n\n"))
            {
                var lower = para.ToLowerInvariant();
                var score = words.Count(lower.Contains);
                if (score > 0) hits.Add((score, rel, para.Length > 1200 ? para[..1200] : para));
            }
        }
        var top = hits.OrderByDescending(h => h.score).Take(6).ToList();
        var first = top.FirstOrDefault().path ?? "none";
        return (top.Count == 0 ? "No passage matches." : string.Join("\n\n", top.Select(h => $"[doc:{h.path}]\n{h.text}")), $"doc:{first}");
    }
}
