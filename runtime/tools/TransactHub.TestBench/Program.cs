using System.Text.Json.Nodes;
using TransactHub.TestBench;

// testbench: the runtime test bench (runtime/testbench/README.md). Exit 0 when every run matches the interpreter and
// what is expected, 1 on any mismatch or failure, 2 on a mistake in the command line.
return await Cli.Start(args);

namespace TransactHub.TestBench
{
    public static class Cli
    {
        const string Usage = """
            Usage: testbench <command>
              run     --profile <name> (--file <path> | --sample <id> | --text <data>) [--bench <name>] [--port port=simulator:scenario ...]
                      [--engines temporal,dtfx,elsa,flowable] [--databases PostgreSql,SqlServer|none] [--tenant t] [--workflow w] [--message m]
                      [--date YYYY-MM-DD] [--temporal test-server|host:port] [--flowable container|url] [--timeout minutes] [--package zip] [--store-release r]
                      [--expect outcome] [--save <case-id>] [--json]
              suite   <folder|case.yaml> [--engines …] [--databases …] [--report suite.md] [--json]
              save    <run-id> --id <case-id> [--title t] [--dir folder]
              profiles | benches
              data    list [--scheme s] [--json]
              data    build --template wps-sif|wps-pif|pain.001|pacs.008 [--tenant t] [--lines n] [--amount a | --min a --max b] [--date d]
                      [--currency c] [--inject a,b] [--seed n] [--employer id] [--out file] [--save <sample-id> [--scenario s] [--expect outcome]]
              data    validate (--file f | --sample id | --text t) [--tenant t] [--rules --profile p]
              serve   [--urls http://127.0.0.1:4380]
            """;

        static string? Opt(string[] a, string k) { var i = Array.IndexOf(a, $"--{k}"); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        static IEnumerable<string> Many(string[] a, string k) { for (var i = 0; i < a.Length - 1; i++) if (a[i] == $"--{k}") yield return a[i + 1]; }
        static JsonArray List(string s) => new(s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => (JsonNode?)x).ToArray());

        public static async Task<int> Start(string[] args)
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
            try
            {
                var home = Home.Find();
                return args[0] switch
                {
                    "run" => await Run(home, args),
                    "suite" => await Suite(home, args),
                    "save" => Save(home, args),
                    "profiles" => Profiles(home),
                    "benches" => Benches(home),
                    "data" => await Data(home, args),
                    "serve" => await Service.Serve(home, args),
                    _ => Bad($"Unknown command {args[0]}"),
                };
            }
            catch (BenchError e) { Console.Error.WriteLine(e.Message); return 1; }
        }

        static int Bad(string why) { Console.Error.WriteLine(why); Console.Error.WriteLine(Usage); return 2; }

        /// <summary>A request from the command line: the profile, the data, and what the options change.</summary>
        public static JsonObject RequestOf(string[] a)
        {
            var req = new JsonObject { ["profile"] = Opt(a, "profile") ?? throw new BenchError("run needs --profile (testbench profiles lists them)") };
            req["data"] = Opt(a, "file") is { } f ? new JsonObject { ["file"] = Path.GetFullPath(f) }
                : Opt(a, "sample") is { } s ? new JsonObject { ["sample"] = s }
                : Opt(a, "text") is { } t ? new JsonObject { ["text"] = t }
                : throw new BenchError("run needs data: --file, --sample or --text");
            foreach (var k in new[] { "bench", "tenant", "workflow", "message" }) if (Opt(a, k) is { } v) req[k] = v;
            if (Opt(a, "engines") is { } en) req["engines"] = List(en);
            if (Opt(a, "databases") is { } db) req["databases"] = List(db);
            var ports = Many(a, "port").ToList();
            if (ports.Count > 0) req["ports"] = new JsonArray(ports.Select(p => (JsonNode?)p).ToArray());
            if (Opt(a, "date") is { } d) req["clock"] = new JsonObject { ["date"] = d };
            var env = new JsonObject();
            if (Opt(a, "temporal") is { } tp) env["temporal"] = tp;
            if (Opt(a, "flowable") is { } fl) env["flowable"] = fl;
            if (Opt(a, "timeout") is { } to) env["engineTimeoutMinutes"] = long.Parse(to);
            if (env.Count > 0) req["environment"] = env;
            if (Opt(a, "package") is { } pkg) req["source"] = new JsonObject { ["kind"] = "packages", ["package"] = Path.GetFullPath(pkg) };
            if (Opt(a, "store-release") is { } sr) req["source"] = new JsonObject { ["kind"] = "store", ["release"] = sr, ["store"] = new JsonObject { ["provider"] = Environment.GetEnvironmentVariable("TH_RELEASE_STORE_PROVIDER") ?? "PostgreSql" } };
            if (Opt(a, "expect") is { } ex) req["expect"] = new JsonObject { ["outcome"] = ex };
            return req;
        }

        static void Print(string s) => Console.Error.WriteLine($"  {s}");

        static async Task<int> Run(Home home, string[] a)
        {
            var catalogue = new Catalogue(home);
            var plan = catalogue.Resolve(RequestOf(a));
            await using var infra = new Infra(home, Print);
            var id = Runner.NewId();
            Console.Error.WriteLine($"run {id}: {plan.Profile}, {plan.Input.Name} ({plan.Input.Kind}), engines {string.Join(", ", plan.Engines)}{(plan.Reference ? " and the interpreter" : "")}, databases {string.Join(", ", plan.Databases)}");
            var result = await new Runner(home, infra).Run(plan, id, Print);
            if (a.Contains("--json")) Console.WriteLine(result.ToJsonString(J.Indented));
            else Console.WriteLine(Report.Markdown(result));
            Console.Error.WriteLine($"reports: {Path.Combine(home.Runs, id)}/report.md, report.html, result.json");
            if (Opt(a, "save") is { } caseId) Console.Error.WriteLine($"saved: {Cases.Save(home, Cases.FromRun(result, caseId, null))}");
            return result["passed"]?.GetValue<bool>() == true ? 0 : 1;
        }

        static JsonObject? Overrides(string[] a)
        {
            var o = new JsonObject();
            if (Opt(a, "engines") is { } en) o["engines"] = List(en);
            if (Opt(a, "databases") is { } db) o["databases"] = List(db);
            return o.Count > 0 ? o : null;
        }

        static async Task<int> Suite(string[] a, Home home, Infra infra)
        {
            var dir = a.Length > 1 && !a[1].StartsWith("--") ? a[1] : home.Suite;
            var s = await Cases.Run(home, infra, Path.GetFullPath(dir), Overrides(a), Print, CancellationToken.None);
            var md = Report.SuiteMarkdown(s);
            if (Opt(a, "report") is { } rf) await File.WriteAllTextAsync(rf, md);
            if (a.Contains("--json")) Console.WriteLine(s.ToJsonString(J.Indented)); else Console.WriteLine(md);
            return s["passed"]?.GetValue<bool>() == true ? 0 : 1;
        }

        static async Task<int> Suite(Home home, string[] a)
        {
            await using var infra = new Infra(home, Print);
            return await Suite(a, home, infra);
        }

        static int Save(Home home, string[] a)
        {
            var runId = a.Length > 1 ? a[1] : throw new BenchError("save needs a run id");
            var file = Path.Combine(home.Runs, runId, "result.json");
            if (!File.Exists(file)) throw new BenchError($"No run {runId} under {home.Runs}");
            var c = Cases.FromRun((JsonObject)JsonNode.Parse(File.ReadAllText(file))!, Opt(a, "id") ?? throw new BenchError("save needs --id"), Opt(a, "title"));
            Console.WriteLine(Cases.Save(home, c, Opt(a, "dir") is { } d ? Path.GetFullPath(d) : null));
            return 0;
        }

        static int Profiles(Home home)
        {
            foreach (var p in new Catalogue(home).Profiles())
                Console.WriteLine($"{J.S(p, "name"),-36} {string.Join(",", J.Strings(p["engines"])),-28} {string.Join(",", J.Strings(p["databases"] ?? p["database"])),-22} {J.S(p, "title")}");
            return 0;
        }

        static int Benches(Home home)
        {
            var c = new Catalogue(home);
            foreach (var kv in c.Benches())
                Console.WriteLine($"{kv.Key,-24} {J.S(kv.Value, "title")}\n    {string.Join("\n    ", c.Bench(kv.Key))}");
            return 0;
        }

        static async Task<int> Data(Home home, string[] a)
        {
            var sub = a.Length > 1 ? a[1] : "list";
            var data = new TestData(home);
            switch (sub)
            {
                case "list":
                    {
                        var entries = data.List(Opt(a, "scheme"));
                        if (a.Contains("--json")) { Console.WriteLine(new JsonArray(entries.Select(e => (JsonNode?)e).ToArray()).ToJsonString(J.Indented)); return 0; }
                        foreach (var e in entries)
                            Console.WriteLine($"{J.S(e, "id"),-30} {J.S(e, "scheme"),-8} {J.S(e, "direction"),-8} expect {J.S(e, "expect"),-10} bench {J.S(e, "bench"),-18} {J.S(e, "title")}");
                        return 0;
                    }
                case "build":
                    {
                        var recipe = new JsonObject { ["template"] = Opt(a, "template") ?? "wps-sif" };
                        foreach (var k in new[] { "tenant", "currency", "employer", "reference" }) if (Opt(a, k) is { } ov) recipe[k == "employer" ? "employerId" : k] = ov;
                        if (Opt(a, "date") is { } dd) recipe["valueDate"] = dd;
                        if (Opt(a, "lines") is { } l) recipe["lines"] = long.Parse(l);
                        if (Opt(a, "seed") is { } sd) recipe["seed"] = long.Parse(sd);
                        if (Opt(a, "amount") is { } am) recipe["amount"] = am;
                        if (Opt(a, "min") is { } mn) recipe["amounts"] = new JsonObject { ["min"] = decimal.Parse(mn, System.Globalization.CultureInfo.InvariantCulture), ["max"] = decimal.Parse(Opt(a, "max") ?? mn, System.Globalization.CultureInfo.InvariantCulture) };
                        if (Opt(a, "inject") is { } inj) recipe["inject"] = List(inj);
                        var built = data.Build(recipe);
                        if (Opt(a, "out") is { } o) { await File.WriteAllTextAsync(o, built.Text); Console.Error.WriteLine($"{o}: {built.Name}"); }
                        else Console.Write(built.Text);
                        var v = data.Validate(Inputs.Detect(built.Text, built.Name), J.S(recipe, "tenant") ?? "example-bank");
                        foreach (var f in v.Format) Console.Error.WriteLine($"format    {f.Code} {f.Where}: {f.Message}");
                        if (Opt(a, "save") is { } sid)
                        {
                            var file = data.Save(sid, built.Text, built.Name, new JsonObject { ["scenario"] = Opt(a, "scenario") ?? string.Join(", ", built.Injected.DefaultIfEmpty("happy path")), ["expect"] = Opt(a, "expect"), ["bench"] = built.SuggestedBench, ["build"] = built.Recipe });
                            Console.Error.WriteLine($"saved: {file}");
                        }
                        return 0;
                    }
                case "validate":
                    {
                        var tenant = Opt(a, "tenant") ?? "example-bank";
                        var d = Opt(a, "file") is { } f ? new JsonObject { ["file"] = Path.GetFullPath(f) } : Opt(a, "sample") is { } s ? new JsonObject { ["sample"] = s } : Opt(a, "text") is { } t ? new JsonObject { ["text"] = t } : throw new BenchError("validate needs --file, --sample or --text");
                        var input = Inputs.Read(d, new Library(home), tenant);
                        var v = data.Validate(input, tenant);
                        foreach (var x in v.Format) Console.WriteLine($"format    {x.Code} {x.Where}: {x.Message}");
                        foreach (var x in v.Synthetic) Console.WriteLine($"synthetic {x.Code} {x.Where}: {x.Message}");
                        var ok = v.Format.Count == 0 && v.Synthetic.Count == 0;
                        if (a.Contains("--rules"))
                        {
                            await using var infra = new Infra(home, Print);
                            var r = await data.Rules(infra, d, Opt(a, "profile") ?? "wps-reference", tenant, Opt(a, "bench"));
                            Console.WriteLine($"rules     the interpreter: {J.S(r, "outcome")} ({string.Join(" | ", J.Strings(r["trace"]).Take(3))}…)");
                            ok &= J.S(r, "outcome") is not null && J.S(r, "error") is null;
                        }
                        Console.WriteLine(ok ? $"{input.Name}: valid ({input.Kind}, {input.Records} record(s))" : $"{input.Name}: not valid");
                        return v.Format.Count == 0 && v.Synthetic.Count == 0 ? 0 : 1;
                    }
                default: return Bad($"Unknown data command {sub}");
            }
        }
    }
}
