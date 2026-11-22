using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.TestBench;

/// <summary>What the store holds after an engine's run, read through EF Core like the rest of the runtime.</summary>
public static class StoreInspector
{
    /// <summary>
    /// The ISO 20022 rows (bulks and payments: ids, amounts, counts, never a party or an account) and every fact chain
    /// verified. A store the harness reported on is read again here, so a run is checked from the database alone.
    /// </summary>
    public static async Task<JsonObject> Inspect(StoreOptions o, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(o);
        var facts = new FactLog(db);
        var bulks = await db.Bulks.AsNoTracking().OrderBy(b => b.Id).ToListAsync(ct);
        var payments = await db.Payments.AsNoTracking().OrderBy(p => p.BulkId).ThenBy(p => p.LineNo).ToListAsync(ct);
        var statuses = await facts.CurrentStatuses(payments.Select(p => p.Id).Concat(bulks.Select(b => b.Id)).ToList(), ct);
        var subjects = await db.Facts.AsNoTracking().GroupBy(f => new { f.SubjectId, f.SubjectKind }).Select(g => new { g.Key.SubjectId, g.Key.SubjectKind, N = g.Count() }).ToListAsync(ct);
        var broken = new JsonArray();
        foreach (var s in subjects.OrderBy(s => s.SubjectId, StringComparer.Ordinal))
        {
            var v = await facts.Verify(s.SubjectId, ct);
            if (!v.Ok) broken.Add(new JsonObject { ["subject"] = s.SubjectId, ["kind"] = s.SubjectKind, ["problem"] = v.Problem });
        }
        return new JsonObject
        {
            ["bulks"] = new JsonArray(bulks.Select(b => (JsonNode?)new JsonObject
            {
                ["id"] = b.Id, ["scheme"] = b.Scheme, ["direction"] = b.Direction, ["messageId"] = b.MessageId, ["numberOfTransactions"] = b.NumberOfTransactions,
                ["controlSum"] = b.ControlSum, ["release"] = b.ReleaseId, ["status"] = statuses.GetValueOrDefault(b.Id),
            }).ToArray()),
            ["payments"] = new JsonArray(payments.Select(p => (JsonNode?)new JsonObject
            {
                ["id"] = p.Id, ["bulk"] = p.BulkId, ["line"] = p.LineNo, ["endToEndId"] = p.EndToEndId, ["amount"] = p.Amount, ["currency"] = p.Currency,
                ["scheme"] = p.Scheme, ["direction"] = p.Direction, ["status"] = statuses.GetValueOrDefault(p.Id),
            }).ToArray()),
            ["parties"] = await db.Parties.CountAsync(ct),
            ["messages"] = await db.Messages.CountAsync(ct),
            ["exceptionItems"] = await db.ExceptionItems.CountAsync(ct),
            ["facts"] = subjects.Sum(s => s.N),
            ["chains"] = new JsonObject { ["subjects"] = subjects.Count, ["verified"] = subjects.Count - broken.Count, ["broken"] = broken, ["ok"] = broken.Count == 0 },
        };
    }
}

/// <summary>An engine's run against the reference: the same outcome and trace, or the difference.</summary>
public static class Compare
{
    public static JsonObject Of(JsonObject? reference, JsonObject run)
    {
        if (reference is null) return new JsonObject { ["compared"] = false, ["same"] = true, ["note"] = "No reference run" };
        var rt = J.Strings(reference["trace"]).ToList();
        var et = J.Strings(run["trace"]).ToList();
        var sameOutcome = J.S(reference, "outcome") == J.S(run, "outcome");
        var sameTrace = rt.SequenceEqual(et);
        var o = new JsonObject
        {
            ["compared"] = true, ["same"] = sameOutcome && sameTrace,
            ["outcome"] = new JsonObject { ["reference"] = J.S(reference, "outcome"), ["engine"] = J.S(run, "outcome"), ["same"] = sameOutcome },
            ["trace"] = new JsonObject { ["same"] = sameTrace, ["diff"] = sameTrace ? new JsonArray() : new JsonArray(Diff(rt, et).Select(d => (JsonNode?)d).ToArray()) },
        };
        // The detail (each subflow's steps) is shown, not judged: the parity suites compare trace and outcome (R-09-07).
        var rd = J.Strings(reference["detail"]).ToList();
        var ed = J.Strings(run["detail"]).ToList();
        o["detail"] = new JsonObject { ["same"] = rd.SequenceEqual(ed), ["diff"] = rd.SequenceEqual(ed) ? new JsonArray() : new JsonArray(Diff(rd, ed).Select(d => (JsonNode?)d).ToArray()) };
        return o;
    }

    /// <summary>A line diff: "  " both, "- " the reference only, "+ " the engine only.</summary>
    public static List<string> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var l = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
            for (var j = b.Count - 1; j >= 0; j--)
                l[i, j] = a[i] == b[j] ? l[i + 1, j + 1] + 1 : Math.Max(l[i + 1, j], l[i, j + 1]);
        var out_ = new List<string>();
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x] == b[y]) { out_.Add("  " + a[x]); x++; y++; }
            else if (l[x + 1, y] >= l[x, y + 1]) out_.Add("- " + a[x++]);
            else out_.Add("+ " + b[y++]);
        }
        while (x < a.Count) out_.Add("- " + a[x++]);
        while (y < b.Count) out_.Add("+ " + b[y++]);
        return out_;
    }

    /// <summary>What a saved case expects, checked against one run (the reference or an engine).</summary>
    public static List<string> Expectation(JsonObject? expect, JsonObject run)
    {
        var f = new List<string>();
        if (expect is null) return f;
        if (J.S(expect, "outcome") is { } eo && J.S(run, "outcome") != eo) f.Add($"outcome {J.S(run, "outcome") ?? "none"}, expected {eo}");
        if (expect["trace"] is JsonArray et && !J.Strings(et).SequenceEqual(J.Strings(run["trace"]))) f.Add("the trace is not the expected trace");
        if (expect["store"] is JsonObject es && run["store"] is JsonObject st)
        {
            foreach (var k in new[] { "bulks", "payments" })
                if (es[k] is JsonValue n && (st["rows"]?[k] as JsonArray)?.Count != J.Long(n)) f.Add($"the store holds {(st["rows"]?[k] as JsonArray)?.Count ?? 0} {k}, expected {n}");
        }
        return f;
    }
}

/// <summary>
/// Runs a plan: the reference on the studio interpreter, then each engine on each database, through the studio's deploy
/// runner and the engine harnesses (runtime/testbench/driver/bench.ts); reads each store; compares; writes the reports.
/// </summary>
public sealed class Runner(Home home, Infra infra)
{
    public static string NewId() => $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    public async Task<JsonObject> Run(Plan plan, string id, Action<string>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var dir = Path.Combine(home.Runs, id);
        Directory.CreateDirectory(dir);
        var work = Path.Combine(dir, "work");
        Directory.CreateDirectory(work);
        var result = new JsonObject { ["id"] = id, ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"), ["plan"] = plan.Json(), ["request"] = plan.Request.DeepClone() };
        void say(string s) { progress?.Invoke(s); }
        var runs = new JsonArray();
        result["runs"] = runs;
        try
        {
            infra.CheckStudio();
            var packages = await Packages(plan, work, ct);
            JsonObject Job(JsonArray engines, bool reference, string wdir)
            {
                var job = new JsonObject
                {
                    ["workflow"] = plan.Workflow, ["tenant"] = plan.Tenant, ["banks"] = new JsonArray(plan.Banks.Select(b => (JsonNode?)b).ToArray()),
                    ["evaluationDate"] = plan.EvaluationDate, ["bench"] = new JsonArray(plan.Bench.Select(b => (JsonNode?)b.Json()).ToArray()),
                    ["reference"] = reference, ["release"] = plan.Release, ["engines"] = engines, ["work"] = wdir,
                };
                if (plan.Input.Payload is not null) job["inbound"] = new JsonObject { ["payload"] = plan.Input.Payload, ["message"] = plan.Input.Message };
                if (plan.Input.Context is not null) job["context"] = plan.Input.Context.DeepClone();
                if (packages is not null) job["packages"] = packages.DeepClone();
                Directory.CreateDirectory(wdir);
                return job;
            }

            // The reference first, on its own: the studio interpreter, in seconds.
            var refDir = Path.Combine(work, "reference");
            var refOut = await Driver(Job(new JsonArray(), plan.Reference, refDir), refDir, "none", say, TimeSpan.FromMinutes(10), ct);
            if (J.S(refOut, "error") is { } rerr) throw new BenchError(rerr);
            result["content"] = J.S(refOut, "content");
            result["workflow"] = J.Clone(refOut["workflow"]);
            if (refOut["reference"] is JsonObject r0) result["reference"] = r0.DeepClone();

            // Then each engine on each database, each in a driver process of its own with its own time limit, so an
            // engine that hangs is stopped and reported without holding up the others.
            foreach (var db in plan.Engines.Length == 0 ? [] : plan.Databases)
            {
                infra.Build(plan.Engines, db != "none");
                foreach (var e in plan.Engines)
                {
                    var env = new JsonObject();
                    StoreOptions? so = null;
                    if (db != "none")
                    {
                        say($"{e} on {db}: database");
                        var name = $"tb_{id.Replace("-", "_")}_{e}".ToLowerInvariant();
                        var conn = await infra.Database(db, name, plan.Connections, ct);
                        env["TH_STORE_PROVIDER"] = db;
                        env["TH_STORE_CONNECTION"] = conn;
                        so = new StoreOptions(Enum.Parse<StoreProvider>(db), conn);
                    }
                    if (e == "flowable") foreach (var kv in await infra.Flowable(plan.Flowable, ct)) env[kv.Key] = kv.Value;
                    if (e == "temporal")
                    {
                        foreach (var kv in infra.Temporal(plan.Temporal)) env[kv.Key] = kv.Value;
                        // On the time-skipping test server a claim check run as a local activity that writes to the store
                        // lets virtual time pass, and the first attempt times out (Issue I-93). The harness's own switch
                        // runs claim checks as regular activities, which hold the clock.
                        if (db != "none" && plan.Temporal == "test-server" && Environment.GetEnvironmentVariable("TH_LOCAL_CLAIMCHECK") is null) env["TH_LOCAL_CLAIMCHECK"] = "0";
                    }
                    var wdir = Path.Combine(work, $"{db}-{e}");
                    JsonObject run;
                    try
                    {
                        var output = await Driver(Job(new JsonArray(new JsonObject { ["engine"] = e, ["env"] = env }), false, wdir), wdir, db, say, plan.EngineTimeout, ct);
                        run = (output["engines"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?.DeepClone() as JsonObject
                            ?? new JsonObject { ["engine"] = e, ["ok"] = false, ["error"] = J.S(output, "error") ?? "The driver reported no run" };
                    }
                    catch (TimeoutException)
                    {
                        say($"{e}: did not finish in {plan.EngineTimeout.TotalMinutes} minutes ({db})");
                        run = new JsonObject { ["engine"] = e, ["ok"] = false, ["error"] = $"did not finish in {plan.EngineTimeout.TotalMinutes} minutes (environment.engineTimeoutMinutes); stopped", ["elapsedMs"] = (long)plan.EngineTimeout.TotalMilliseconds };
                    }
                    run["database"] = db;
                    run["timings"] = new JsonObject { ["totalMs"] = run["elapsedMs"]?.DeepClone(), ["harnessMs"] = run["harnessMs"]?.DeepClone() };
                    run.Remove("elapsedMs");
                    run.Remove("harnessMs");
                    if (so is not null && J.S(run, "error") is null)
                    {
                        var report = run["store"] as JsonObject ?? new JsonObject();
                        try { report["rows"] = await StoreInspector.Inspect(so, ct); }
                        catch (Exception ex) { report["rows"] = new JsonObject { ["error"] = ex.Message }; }
                        report["database"] = so.ConnectionString.Split(';').FirstOrDefault(p => p.StartsWith("Database=", StringComparison.Ordinal))?[9..];
                        run["store"] = report;
                    }
                    runs.Add(run);
                }
            }
        }
        catch (BenchError e) { result["error"] = e.Message; }
        catch (OperationCanceledException) { result["error"] = "cancelled"; }
        Judge(result);
        result["elapsedMs"] = sw.ElapsedMilliseconds;
        await File.WriteAllTextAsync(Path.Combine(dir, "result.json"), result.ToJsonString(J.Indented), ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "report.md"), Report.Markdown(result), ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "report.html"), Report.Html(result), ct);
        if (Environment.GetEnvironmentVariable("TH_TESTBENCH_KEEP_WORK") != "1") { try { Directory.Delete(work, true); } catch (IOException) { } }
        return result;
    }

    /// <summary>Every check of a run: errors, each engine against the reference, the store, and what the request expects.</summary>
    public static void Judge(JsonObject result)
    {
        var failures = new JsonArray();
        if (J.S(result, "error") is { } e) failures.Add(e);
        var reference = result["reference"] as JsonObject;
        var expect = result["plan"]?["expect"] as JsonObject;
        if (reference is not null)
        {
            if (J.S(reference, "error") is { } re) failures.Add($"interpreter: {re}");
            foreach (var f in Compare.Expectation(expect, reference)) failures.Add($"interpreter: {f}");
            reference["expectation"] = new JsonObject { ["failures"] = new JsonArray(Compare.Expectation(expect, reference).Select(x => (JsonNode?)x).ToArray()) };
        }
        foreach (var run in (result["runs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var label = $"{J.S(run, "engine")} on {J.S(run, "database")}";
            if (J.S(run, "error") is { } err) { failures.Add($"{label}: {err.Split('\n')[0]}"); run["failures"] = new JsonArray(err.Split('\n')[0]); run["passed"] = false; continue; }
            var cmp = Compare.Of(reference, run);
            run["comparison"] = cmp;
            var mine = new List<string>();
            if (cmp["same"]?.GetValue<bool>() == false)
            {
                if (cmp["outcome"]?["same"]?.GetValue<bool>() == false) mine.Add($"outcome {J.S(run, "outcome")}, the interpreter's is {J.S(reference, "outcome")}");
                if (cmp["trace"]?["same"]?.GetValue<bool>() == false) mine.Add("the trace is not the interpreter's");
            }
            if (run["store"] is JsonObject st)
            {
                if (st["ok"] is JsonValue ok && !ok.GetValue<bool>()) mine.Add("the store's payloads do not rebuild to what the engine held");
                if (st["payloads"] is JsonArray pl && pl.OfType<JsonObject>().Any(p => p["chain"]?.GetValue<bool>() == false)) mine.Add("a payload's fact chain is broken");
                if (st["rows"]?["chains"]?["ok"] is JsonValue cok && !cok.GetValue<bool>()) mine.Add("a fact chain in the store is broken");
                if (st["rows"]?["error"] is { } rerr) mine.Add($"the store could not be read: {rerr}");
            }
            else if (J.S(run, "database") is { } d && d != "none") mine.Add("the engine reported no store");
            mine.AddRange(Compare.Expectation(expect, run));
            run["failures"] = new JsonArray(mine.Select(x => (JsonNode?)x).ToArray());
            run["passed"] = mine.Count == 0;
            foreach (var m in mine) failures.Add($"{label}: {m}");
        }
        result["failures"] = failures;
        result["passed"] = failures.Count == 0;
    }

    /// <summary>The packages of a stored release (one per engine, scope workflow/engine) or of the zips the request names.</summary>
    async Task<JsonObject?> Packages(Plan plan, string work, CancellationToken ct)
    {
        var kind = J.S(plan.Source, "kind") ?? (plan.Source["store"] is not null ? "store" : plan.Source["packages"] is not null || plan.Source["package"] is not null ? "packages" : "content");
        if (kind == "content") return null;
        var out_ = new JsonObject();
        if (kind == "packages")
        {
            if (J.S(plan.Source, "package") is { } one) foreach (var e in plan.Engines) out_[e] = Path.GetFullPath(one);
            foreach (var kv in plan.Source["packages"] as JsonObject ?? []) out_[kv.Key] = Path.GetFullPath(kv.Value!.ToString());
            return out_;
        }
        if (kind != "store") throw new BenchError($"No source {kind}: content, store or packages");
        var store = plan.Source["store"] as JsonObject ?? throw new BenchError("source.store names the release store: { provider, connection }");
        var release = plan.Release ?? throw new BenchError("A stored release is named by its release: source.release");
        var o = new StoreOptions(Enum.Parse<StoreProvider>(J.S(store, "provider") ?? "PostgreSql", true), J.S(store, "connection") ?? Environment.GetEnvironmentVariable("TH_RELEASE_STORE") ?? throw new BenchError("source.store.connection, or TH_RELEASE_STORE"));
        await using var db = StoreDbContext.Open(o);
        var registry = new ReleaseRegistry(db);
        foreach (var e in plan.Engines)
        {
            var row = await registry.Get($"{plan.Workflow}/{e}", release, ct) ?? throw new BenchError($"The store holds no release {plan.Workflow}/{e}@{release}");
            var f = Path.Combine(work, $"{e}-{release}.zip");
            await File.WriteAllBytesAsync(f, row.Package, ct);
            out_[e] = f;
        }
        return out_;
    }

    async Task<JsonObject> Driver(JsonObject job, string work, string db, Action<string> say, TimeSpan limit, CancellationToken ct)
    {
        var jobFile = Path.Combine(work, "job.json");
        var outFile = Path.Combine(work, "result.json");
        await File.WriteAllTextAsync(jobFile, job.ToJsonString(), ct);
        var psi = new ProcessStartInfo(Path.Combine(home.Studio, "node_modules", ".bin", "tsx"), [home.Driver, jobFile, outFile])
        {
            WorkingDirectory = home.Studio, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        using var p = Process.Start(psi)!;
        var errors = new System.Text.StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.AppendLine(e.Data); };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } l && l.StartsWith("PROGRESS ", StringComparison.Ordinal))
                say(db == "none" ? JsonNode.Parse(l[9..])!.ToString() : $"{JsonNode.Parse(l[9..])} ({db})");
        };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            await p.WaitForExitAsync(CancellationToken.None);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException();
        }
        if (!File.Exists(outFile)) throw new BenchError($"The driver exited {p.ExitCode} without a result: {errors.ToString()[^Math.Min(errors.Length, 3000)..]}");
        return JsonNode.Parse(await File.ReadAllTextAsync(outFile, ct)) as JsonObject ?? throw new BenchError("The driver's result is not JSON");
    }

    public static string Ms(JsonNode? n) => n is JsonValue v && J.Long(v) is long ms ? (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s" : "";
}
