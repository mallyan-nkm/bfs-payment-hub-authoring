using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TransactHub.TestBench;

/// <summary>One port of a bench: which simulator plays it, in which scenario.</summary>
public sealed record BenchPort(string Port, string Simulator, string Scenario)
{
    public override string ToString() => $"{Port}={Simulator}:{Scenario}";

    public static BenchPort Parse(string s)
    {
        var m = Regex.Match(s.Trim(), @"^([^=\s]+)=([^:\s]+):(\S+)$");
        if (!m.Success) throw new BenchError($"A port is written port=simulator:scenario, not {s}");
        return new(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
    }

    public static BenchPort From(JsonNode? n) => n is JsonValue ? Parse(n.ToString()) :
        new(J.S(n, "port") ?? throw new BenchError("A bench port names no port"), J.S(n, "simulator") ?? throw new BenchError("A bench port names no simulator"), J.S(n, "scenario") ?? throw new BenchError("A bench port names no scenario"));

    public JsonObject Json() => new() { ["port"] = Port, ["simulator"] = Simulator, ["scenario"] = Scenario };
}

/// <summary>Everything a run needs, resolved from a profile, the request and the data.</summary>
public sealed class Plan
{
    public required string Profile { get; init; }
    public required string Workflow { get; init; }
    public string? Release { get; init; }
    public required string Tenant { get; init; }
    public required string[] Banks { get; init; }
    public required string[] Engines { get; init; }
    public required bool Reference { get; init; }
    /// <summary>PostgreSql, SqlServer, or none (DTFx and Elsa then run the studio's own harnesses; Temporal and Flowable keep payloads in memory).</summary>
    public required string[] Databases { get; init; }
    public required Dictionary<string, string> Connections { get; init; }
    public required string BenchName { get; init; }
    public required List<BenchPort> Bench { get; init; }
    public required string EvaluationDate { get; init; }
    /// <summary>When the run starts that day, HH:MM:SS UTC (clock.time); null for the studio's default, 06:00 (R-12-28: a cut-off is a time of day).</summary>
    public string? StartTime { get; init; }
    /// <summary>Values the request gives beside the bank's configuration (values: parameter, value, validFrom), on the interpreter and every engine.</summary>
    public JsonArray? Values { get; init; }
    /// <summary>test-server (Temporal's time-skipping server, started by the harness) or host:port of a Temporal server.</summary>
    public required string Temporal { get; init; }
    /// <summary>container (a Flowable REST container for the run) or the URL of a Flowable REST server.</summary>
    public required string Flowable { get; init; }
    /// <summary>How long one engine's run may take before it is stopped (environment.engineTimeoutMinutes, default 15).</summary>
    public TimeSpan EngineTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public required JsonObject Source { get; init; }
    public required Input Input { get; init; }
    public JsonObject? Expect { get; init; }
    public required JsonObject Request { get; init; }

    public JsonObject Json() => new()
    {
        ["profile"] = Profile, ["workflow"] = Workflow, ["release"] = Release, ["tenant"] = Tenant, ["banks"] = new JsonArray(Banks.Select(b => (JsonNode?)b).ToArray()),
        ["engines"] = new JsonArray(Engines.Select(e => (JsonNode?)e).ToArray()), ["reference"] = Reference,
        ["databases"] = new JsonArray(Databases.Select(d => (JsonNode?)d).ToArray()),
        ["bench"] = BenchName, ["ports"] = new JsonArray(Bench.Select(b => (JsonNode?)b.ToString()).ToArray()),
        ["clock"] = new JsonObject { ["date"] = EvaluationDate, ["time"] = StartTime }, ["values"] = J.Clone(Values),
        ["environment"] = new JsonObject { ["temporal"] = Temporal, ["flowable"] = Flowable, ["engineTimeoutMinutes"] = (long)EngineTimeout.TotalMinutes },
        ["source"] = J.Clone(Source), ["input"] = Input.Summary(), ["expect"] = J.Clone(Expect),
    };
}

/// <summary>The profiles, the named benches and the sample library under runtime/testbench, and how a request resolves against them.</summary>
public sealed class Catalogue(Home home)
{
    public static readonly string[] AllEngines = ["temporal", "dtfx", "elsa", "flowable"];
    /// <summary>
    /// The default engine (owner's decision of 23 November 2026, R-09-79): Temporal, on the Temporal SQL Server plugin in a
    /// deployment. A run whose profile and request name no engines runs on it; a tester may still start DTFx, Elsa or Flowable.
    /// </summary>
    public static readonly string[] DefaultEngines = ["temporal"];
    public static readonly string[] AllDatabases = ["PostgreSql", "SqlServer", "none"];
    public Home Home => home;

    public IEnumerable<JsonObject> Profiles() =>
        Directory.EnumerateFiles(home.Profiles, "*.yaml").Order(StringComparer.Ordinal).Select(f => Yaml.Load(f));

    public JsonObject Profile(string name) =>
        Profiles().FirstOrDefault(p => J.S(p, "name") == name)
        ?? throw new BenchError($"No profile {name}. The profiles are: {string.Join(", ", Profiles().Select(p => J.S(p, "name")))}");

    public JsonObject Benches() => Yaml.Load(Path.Combine(home.Bench, "benches.yaml"))["benches"] as JsonObject ?? throw new BenchError("benches.yaml has no benches");

    /// <summary>A named bench, its <c>extends</c> chain applied: a later port replaces an earlier one.</summary>
    public List<BenchPort> Bench(string name, int depth = 0)
    {
        if (depth > 8) throw new BenchError($"The bench {name} extends itself");
        var b = Benches()[name] as JsonObject ?? throw new BenchError($"No bench {name}. The benches are: {string.Join(", ", Benches().Select(kv => kv.Key))}");
        var ports = J.S(b, "extends") is { } parent ? Bench(parent, depth + 1) : [];
        foreach (var p in b["ports"] as JsonArray ?? []) ports = Override(ports, BenchPort.From(p));
        return ports;
    }

    public static List<BenchPort> Override(List<BenchPort> ports, BenchPort p) => [.. ports.Where(x => x.Port != p.Port), p];

    public Library Library() => new(home);

    /// <summary>The bank packs a run loads: those the profile or request names, and the tenant's own folder (the example bank's is always loaded).</summary>
    public string[] BanksFor(string tenant, IEnumerable<string> named)
    {
        var banks = named.ToList();
        var dir = Path.Combine(home.Repo, "content-configurations", "banks");
        if (tenant != "example-bank" && Directory.Exists(dir))
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var cs = Path.Combine(d, "configuration-set.yaml");
                if (File.Exists(cs) && J.S(Yaml.Load(cs), "tenant") == tenant && !banks.Contains(Path.GetFileName(d))) banks.Add(Path.GetFileName(d));
            }
        return [.. banks];
    }

    /// <summary>Resolves a request (a profile and what the request says beside it) into a plan.</summary>
    public Plan Resolve(JsonObject request)
    {
        var profileName = J.S(request, "profile") ?? throw new BenchError("A request names a profile (testbench profiles lists them)");
        var profile = Profile(profileName);
        string? pick(string key) => J.S(request, key) ?? J.S(profile, key);
        JsonNode? node(string key) => request[key] ?? profile[key];

        var data = request["data"] as JsonObject ?? (request["data"] is JsonValue v ? new JsonObject { ["text"] = v.ToString() } : null)
            ?? throw new BenchError("A request sends data: data.file, data.text, data.base64 or data.sample");
        var libEntry = J.S(data, "sample") is { } sid ? Library().Entry(sid) : null;
        var tenant = pick("tenant") ?? "example-bank";
        var input = Inputs.Read(data, Library(), tenant);

        var benchName = J.S(request, "bench") ?? J.S(libEntry, "bench") ?? J.S(profile, "bench") ?? throw new BenchError($"The profile {profileName} names no bench, and the request names none");
        var bench = Bench(benchName);
        foreach (var p in J.Strings(profile["ports"]).Concat(J.Strings(libEntry?["ports"])).Concat(J.Strings(request["ports"]))) bench = Override(bench, BenchPort.Parse(p));

        var engines = (node("engines") is { } en ? J.Strings(en) : DefaultEngines).Select(e => e.ToLowerInvariant()).ToArray();
        foreach (var e in engines) if (!AllEngines.Contains(e)) throw new BenchError($"No engine {e}: the engines are {string.Join(", ", AllEngines)} (and the studio interpreter as the reference)");
        var dbs = (request["databases"] ?? request["database"] ?? profile["databases"] ?? profile["database"]) is { } dn ? J.Strings(dn).ToArray() : ["PostgreSql"];
        dbs = dbs.Select(d => AllDatabases.FirstOrDefault(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase)) ?? throw new BenchError($"No database {d}: PostgreSql, SqlServer or none")).ToArray();
        var envNode = (JsonObject?)J.Clone(profile["environment"] as JsonObject) ?? new JsonObject();
        foreach (var kv in request["environment"] as JsonObject ?? []) envNode[kv.Key] = J.Clone(kv.Value);
        var connections = new Dictionary<string, string>();
        foreach (var kv in envNode["connections"] as JsonObject ?? []) if (kv.Value is not null) connections[kv.Key] = kv.Value.ToString();
        foreach (var p in new[] { "PostgreSql", "SqlServer" })
            if (Environment.GetEnvironmentVariable($"TH_TESTBENCH_{p.ToUpperInvariant()}") is { Length: > 0 } c) connections.TryAdd(p, c);

        var workflow = J.S(request, "workflow") ?? J.S(libEntry, "workflow") ?? J.S(profile, "workflow") ?? throw new BenchError($"The profile {profileName} names no workflow");
        var message = J.S(request, "message") ?? J.S(libEntry, "message") ?? input.Message ?? J.S(profile, "message");
        if (input.Payload is not null && message is null) throw new BenchError("The data is a file, and neither the profile nor the request names its start message (for example uae.wps.sif)");
        var clock = (request["clock"] ?? profile["clock"]) as JsonObject;
        var expect = (request["expect"] as JsonObject) ?? (libEntry?["expect"] is { } le ? (le is JsonValue ? new JsonObject { ["outcome"] = le.ToString() } : (JsonObject)le.DeepClone()) : null);
        var source = (request["source"] ?? profile["source"]) switch
        {
            null => new JsonObject { ["kind"] = "content" },
            JsonValue sv => new JsonObject { ["kind"] = sv.ToString() },
            JsonObject so => (JsonObject)so.DeepClone(),
            _ => throw new BenchError("source is content, or { store: …, release } or { packages: { engine: zip } }"),
        };
        return new Plan
        {
            Profile = profileName, Workflow = workflow, Release = J.S(request, "release") ?? J.S(source, "release"),
            Tenant = tenant, Banks = BanksFor(tenant, J.Strings(node("banks"))),
            Engines = engines, Reference = (node("reference") as JsonValue)?.GetValue<bool>() ?? true,
            Databases = dbs, Connections = connections, BenchName = benchName, Bench = bench,
            EvaluationDate = J.S(clock, "date") ?? "2026-11-17",
            StartTime = J.S(clock, "time") is { Length: > 0 } t ? (System.Text.RegularExpressions.Regex.IsMatch(t, @"^([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?$") ? (t.Length == 5 ? $"{t}:00" : t) : throw new BenchError($"clock.time {t} is not HH:mm or HH:mm:ss (UTC)")) : null,
            Values = (request["values"] ?? profile["values"]) as JsonArray is { } vs ? (JsonArray)vs.DeepClone() : null,
            Temporal = Environment.GetEnvironmentVariable("TH_TEMPORAL_ADDRESS") is { Length: > 0 } ta ? ta : J.S(envNode, "temporal") ?? "test-server",
            Flowable = Environment.GetEnvironmentVariable("TH_FLOWABLE_URL") is { Length: > 0 } fu ? fu : J.S(envNode, "flowable") ?? "container",
            EngineTimeout = TimeSpan.FromMinutes(J.Long(envNode["engineTimeoutMinutes"]) ?? 15),
            Source = source, Input = input with { Message = message }, Expect = expect, Request = (JsonObject)request.DeepClone(),
        };
    }
}
