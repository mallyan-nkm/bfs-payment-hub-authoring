using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TransactHub.Engine.Temporal;
using TransactHub.Engines;
using TransactHub.Engines.Durable;
using TransactHub.Model;
using TransactHub.Telemetry;
using Xunit;

namespace TransactHub.Store.Tests;

// Monitoring of the runtime (R-09-31 to R-09-36, I-66): a WPS salary file's attempts through the Temporal host, over
// HTTP to a worker, with its payload in the store, emit one trace (intake, attempts, store writes), the payments,
// activity, queue and store metrics, and logs carrying the trace id; and nothing an exporter receives holds a value
// from the file's party records. The exporters are OpenTelemetry's in-memory ones, behind the masking processors.

[Collection(DatabaseCollection.Name)]
public sealed class TelemetryTests(Databases dbs)
{
    /// <summary>The synthetic salary file of ClaimCheckTests, after intake.</summary>
    static JsonObject SalaryFile() => (JsonObject)JsonNode.Parse("""
    {
      "header": { "paymentId": "P-1", "scheme": "uae.wps", "direction": "outward", "conversation": "conv-1" },
      "batch": { "id": "0000001234567261117083000", "categoryPurpose": "SALA", "controlSum": 1500, "currency": "AED", "numberOfTransactions": 2,
                 "debtor": { "name": "Example Trading LLC", "postalAddress": { "townName": "Dubai", "country": "AE" } },
                 "debtorAccount": { "iban": "AE070000000000000000001" }, "debtorAgent": { "clearingMemberId": "302620122" } },
      "items": [
        { "payment": { "creditor": { "name": "Employee One" }, "creditorAgent": { "clearingMemberId": "203320101" }, "creditorAccount": { "iban": "AE070331234567890123456" }, "instructedAmount": { "value": 1000, "currency": "AED" } },
          "ext": { "uae": { "wps": { "personId": "00001234567890", "fixedPay": 800, "variablePay": 200, "leaveDays": 0 } } } },
        { "payment": { "creditor": { "name": "Employee Two" }, "creditorAgent": { "clearingMemberId": "203320101" }, "creditorAccount": { "iban": "AE070331234567890123457" }, "instructedAmount": { "value": 500.25, "currency": "AED" } },
          "ext": { "uae": { "wps": { "personId": "00001234567891", "fixedPay": 500.25, "variablePay": 0, "leaveDays": 2 } } } }
      ],
      "ext": { "uae": { "wps": { "employerId": "0000001234567", "salaryMonth": "102026" } } }
    }
    """)!;

    /// <summary>Every value of the file's party records: none may reach an exporter.</summary>
    static readonly string[] PartyValues =
    [
        "Example Trading LLC", "Employee One", "Employee Two", "AE070000000000000000001", "AE070331234567890123456", "AE070331234567890123457",
        "00001234567890", "00001234567891", "0000001234567",
    ];

    /// <summary>A deployed worker, as far as the host can tell: it answers attempts, echoing the item it was given, and fails one.</summary>
    sealed class FakeWorker : IDisposable
    {
        readonly HttpListener listener = new();
        public readonly string Url;
        public readonly ConcurrentBag<string> TraceParents = new();

        public FakeWorker()
        {
            var port = 7800 + Random.Shared.Next(800);
            Url = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(Url);
            listener.Start();
            _ = Task.Run(Serve);
        }

        async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch { return; }
                if (c.Request.Headers["traceparent"] is string tp) TraceParents.Add(tp);
                var body = (JsonObject)JsonNode.Parse(await new StreamReader(c.Request.InputStream).ReadToEndAsync())!;
                var item = J.P(J.P(body, "ctx"), "__item");
                var iban = J.S(J.P(J.P(item, "payment"), "creditorAccount"), "iban");
                JsonObject res = J.S(body, "step") == "post"
                    // A worker's error may quote what it was given; the host keeps only its name.
                    ? new() { ["kind"] = "error", ["name"] = "AccountClosed", ["retryable"] = false, ["message"] = $"account {iban} of Employee One is closed" }
                    : new() { ["kind"] = "result", ["outcome"] = "pass", ["writes"] = new JsonObject { ["checked"] = iban ?? "file" } };
                var bytes = Encoding.UTF8.GetBytes(res.ToJsonString());
                c.Response.ContentType = "application/json";
                await c.Response.OutputStream.WriteAsync(bytes);
                c.Response.Close();
            }
        }

        public void Dispose() { listener.Stop(); listener.Close(); }
    }

    sealed class Exported
    {
        public readonly List<Activity> Spans = new();
        public readonly List<Metric> Metrics = new();
        public readonly List<LogRecord> Logs = new();
    }

    static TelemetryHost Start(Exported into) => TelemetryHost.Start("transacthub-test",
        traces: t => t.AddInMemoryExporter(into.Spans),
        metrics: m => m.AddInMemoryExporter(into.Metrics),
        logs: l => l.AddInMemoryExporter(into.Logs));

    /// <summary>Every string an exporter was handed: span names, attributes, events, status; log bodies, attributes, exceptions; metric names and tags.</summary>
    static List<string> Strings(Exported e)
    {
        var all = new List<string>();
        foreach (var s in e.Spans)
        {
            all.Add(s.DisplayName);
            all.Add(s.StatusDescription ?? "");
            foreach (var (k, v) in s.TagObjects) all.Add($"{k}={v}");
            foreach (var ev in s.Events) { all.Add(ev.Name); foreach (var (k, v) in ev.Tags) all.Add($"{k}={v}"); }
        }
        foreach (var r in e.Logs)
        {
            all.Add(r.FormattedMessage ?? "");
            all.Add(r.Body ?? "");
            foreach (var (k, v) in r.Attributes ?? []) all.Add($"{k}={v}");
            if (r.Exception is { } x) all.Add(x.ToString());
        }
        foreach (var m in e.Metrics)
        {
            all.Add(m.Name);
            foreach (ref readonly var p in m.GetMetricPoints())
                foreach (var (k, v) in p.Tags) all.Add($"{k}={v}");
        }
        return all;
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_WPS_salary_file_through_the_Temporal_host_emits_one_trace_the_metrics_and_correlated_logs(StoreProvider p)
    {
        var exported = new Exported();
        using var telemetry = Start(exported);
        EngineTelemetry.LogSink = telemetry.Sink(EngineTelemetry.SourceName);
        EngineTelemetry.Engine = "temporal";
        var o = dbs.Fresh(p);
        await using (var db = StoreDbContext.Open(o)) await db.Database.MigrateAsync();
        using var worker = new FakeWorker();
        var plan = new JsonObject
        {
            ["tenant"] = "example-bank",
            ["built"] = new JsonObject { ["workflow"] = "p.wf.corporate.v1", ["config"] = new JsonObject { ["tenant"] = "example-bank" }, ["context"] = SalaryFile() },
        };
        string traceId;
        // The intake: the deploy runner's span, the parent of everything the run does (TRACEPARENT across processes).
        using (var intake = RuntimeTelemetry.Source.StartActivity("th.payment.intake"))
        {
            Assert.NotNull(intake);
            traceId = intake.TraceId.ToHexString();
            using var run = EngineTelemetry.StartRun("temporal", plan);
            var dep = new Deployment(new JsonObject(), new JsonObject { ["tenant"] = "example-bank" }, 0, 100);
            using var party = new WorkerRouter(new JsonObject { ["th-uae-wps"] = worker.Url }, new JsonObject { ["uae.wps"] = "th-uae-wps" });
            var host = new Host(party, dep, new DurableClaimCheck(o));
            var (state, payload) = ClaimCheckStore.Split(SalaryFile());
            await host.Store.Create("root-1#ctx", payload, new AcceptInfo("example-bank", "uae.wps", Direction.Outward, "p.wf.corporate.v1@1.0.0"));
            JsonObject Attempt(string step, string activity, int? item = null) => new()
            {
                ["instance"] = "root-1", ["activity"] = activity, ["release"] = "1.0.0", ["step"] = step, ["call"] = 1, ["attempt"] = 1, ["now"] = 0,
                ["payloadRef"] = "root-1#ctx", ["state"] = J.Clone(state), ["item"] = item, ["level"] = item is null ? null : "item",
            };
            var validate = await host.Attempt(Attempt("validate", "uae.wps.act.validate-salary-file.v1"));
            Assert.Equal("result", J.S(validate, "kind"));
            await host.ClaimCheckOp(new JsonObject { ["op"] = "commit", ["token"] = J.S(validate, "token") });
            for (var i = 0; i < 2; i++)
            {
                var r = await host.Attempt(Attempt("record-checks", "uae.wps.act.record-checks.v1", i));
                await host.ClaimCheckOp(new JsonObject { ["op"] = "commit", ["token"] = J.S(r, "token") });
            }
            var post = await host.Attempt(Attempt("post", "uae.wps.act.post.v1"));
            Assert.Equal("error", J.S(post, "kind"));
            EngineTelemetry.Observed(plan, new JsonObject { ["instances"] = new JsonArray(new JsonObject { ["trace"] = new JsonArray("validate:pass", "end:settled") }) }, TimeSpan.FromSeconds(1));
            EngineTelemetry.Log(1, "Masking holds for {th.reason} and {creditorName} in {th.note}", "account AE070331234567890123456", "Employee One", "id 00001234567890");
        }
        telemetry.Tracer!.ForceFlush();
        telemetry.MeterProvider!.ForceFlush();
        telemetry.Dispose();
        EngineTelemetry.LogSink = null;

        // One trace: the intake, the run, each attempt, each store write.
        var mine = exported.Spans.Where(s => s.TraceId.ToHexString() == traceId).ToList();
        var names = mine.Select(s => s.DisplayName).ToList();
        Assert.Contains("th.payment.intake", names);
        Assert.Contains("th.engine.run", names);
        Assert.Contains("th.attempt uae.wps.act.validate-salary-file.v1", names);
        Assert.Equal(2, names.Count(n => n == "th.attempt uae.wps.act.record-checks.v1"));
        Assert.Contains(names, n => n.StartsWith("th.store.write ") && n.Contains("bulk"));
        Assert.True(names.Count(n => n == "th.store.write fact" || n == "th.store.write fact+outbox") >= 6, string.Join(", ", names));
        var attempt = mine.First(s => s.DisplayName == "th.attempt uae.wps.act.validate-salary-file.v1");
        Assert.Equal("temporal", attempt.GetTagItem("th.engine"));
        Assert.Equal("P-1", attempt.GetTagItem("th.payment.id"));
        Assert.Equal("th-uae-wps", attempt.GetTagItem("th.queue"));
        var failed = mine.First(s => s.DisplayName == "th.attempt uae.wps.act.post.v1");
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
        Assert.Equal("AccountClosed", failed.GetTagItem("th.error"));
        Assert.All(mine.Where(s => s.DisplayName.StartsWith("th.store.write")), s => Assert.Equal(p.ToString(), s.GetTagItem("th.store.provider")));
        // The worker was handed the trace with every attempt.
        Assert.Equal(4, worker.TraceParents.Count(tp => tp.Contains(traceId)));

        // The metrics.
        var metrics = exported.Metrics.Select(m => m.Name).ToHashSet();
        foreach (var m in new[] { "th.payments", "th.payment.lines", "th.payment.duration", "th.activity.duration", "th.activity.errors", "th.queue.in_flight", "th.store.write.duration", "th.store.rows" })
            Assert.Contains(m, metrics);
        var payments = exported.Metrics.Last(m => m.Name == "th.payments");
        var tags = new Dictionary<string, object?>();
        foreach (ref readonly var pt in payments.GetMetricPoints()) foreach (var (k, v) in pt.Tags) tags[k] = v;
        Assert.Equal("settled", tags["th.outcome"]);
        Assert.Equal("uae.wps", tags["th.scheme"]);
        Assert.Equal("example-bank", tags["th.tenant"]);
        Assert.Equal("temporal", tags["th.engine"]);

        // The logs carry the trace they were written in.
        Assert.Contains(exported.Logs, l => l.TraceId.ToHexString() == traceId && (l.FormattedMessage ?? "").Contains("Run started on temporal"));
        Assert.Contains(exported.Logs, l => l.TraceId.ToHexString() == traceId && (l.FormattedMessage ?? "").Contains("ended in AccountClosed"));

        // Nothing an exporter saw holds a value of the party records.
        var leaks = Strings(exported).Where(s => PartyValues.Any(s.Contains)).ToList();
        Assert.True(leaks.Count == 0, "personal data in telemetry:\n" + string.Join('\n', leaks));
    }

    [Fact]
    public void Masking_replaces_personal_keys_and_masks_IBANs_long_numbers_and_emails_in_spans_events_and_logs()
    {
        Assert.Equal("****3456", Masking.Scrub("AE070331234567890123456"));
        Assert.Equal("employee ****7890 paid", Masking.Scrub("employee 00001234567890 paid"));
        Assert.Equal("write to [masked-email]", Masking.Scrub("write to someone@example.com"));
        // A long text with no address in it is masked in linear time (review 2026-11, ReDoS): a megabyte in well under a second.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Masking.Scrub(new string('a', 1_000_000) + "@");
        Masking.Scrub(new string('1', 1_000_000) + "x");
        Assert.True(sw.ElapsedMilliseconds < 3000, $"masking a long text took {sw.ElapsedMilliseconds} ms");
        Assert.Equal("call ****4567", Masking.Scrub("call +971 50 123 4567"));
        Assert.Equal("p.port.core-banking.v1 at 2026-11-17 step 12", Masking.Scrub("p.port.core-banking.v1 at 2026-11-17 step 12"));
        Assert.Equal("root-1a2b3c4d", Masking.Scrub("root-1a2b3c4d"));
        Assert.True(Masking.IsPersonalKey("creditor.name"));
        Assert.True(Masking.IsPersonalKey("th.party.iban"));
        Assert.True(Masking.IsPersonalKey("debtorAccount"));
        Assert.False(Masking.IsPersonalKey("th.activity"));
        Assert.False(Masking.IsPersonalKey("db.name"));

        var exported = new Exported();
        using (var telemetry = Start(exported))
        {
            using (var a = RuntimeTelemetry.Source.StartActivity("th.test"))
            {
                Assert.NotNull(a);
                a.SetTag("creditor.name", "Employee One");
                a.SetTag("th.note", "to AE070331234567890123456");
                a.SetTag("th.lines", 2);
                a.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection { ["exception.type"] = "X", ["exception.message"] = "account AE070331234567890123456 closed", ["creditor.name"] = "Employee Two" }));
                a.SetStatus(ActivityStatusCode.Error, "employee 00001234567890 failed");
            }
            var log = telemetry.Loggers.CreateLogger("test");
            log.LogWarning(new InvalidOperationException("IBAN AE070331234567890123457"), "Paid {creditorName} on {th.account}", "Employee Two", "AE070000000000000000001");
            telemetry.Tracer!.ForceFlush();
        }
        var span = Assert.Single(exported.Spans);
        Assert.Equal(Masking.Masked, span.GetTagItem("creditor.name"));
        Assert.Equal("to ****3456", span.GetTagItem("th.note"));
        Assert.Equal(2, span.GetTagItem("th.lines"));
        Assert.Equal("employee ****7890 failed", span.StatusDescription);
        var ev = Assert.Single(span.Events);
        Assert.Equal("account ****3456 closed", ev.Tags.First(t => t.Key == "exception.message").Value);
        Assert.Equal(Masking.Masked, ev.Tags.First(t => t.Key == "creditor.name").Value);
        Assert.Null(span.GetTagItem("th.events.unmasked"));
        var leaks = Strings(exported).Where(s => PartyValues.Any(s.Contains) || s.Contains("AE070331234567890123457") || s.Contains("Employee Two")).ToList();
        Assert.True(leaks.Count == 0, string.Join('\n', leaks));
    }

    static string Repo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "runtime", "observability"))) return d.FullName;
        throw new DirectoryNotFoundException("the repository root");
    }

    [Fact]
    public void The_local_stack_pins_every_image_and_its_dashboards_read_only_metrics_the_hosts_emit()
    {
        var root = Repo();
        var obs = Path.Combine(root, "runtime", "observability");
        // Every image by tag and digest (R-09-37).
        var images = File.ReadAllLines(Path.Combine(obs, "docker-compose.yml")).Select(l => l.Trim()).Where(l => l.StartsWith("image:")).ToList();
        Assert.Equal(5, images.Count);
        Assert.All(images, i => Assert.Matches(@"^image: [a-z0-9./-]+:[0-9A-Za-z.]+@sha256:[0-9a-f]{64}$", i));

        // The instruments the hosts define: the runtime's (System.Diagnostics.Metrics) and the studio's (OpenTelemetry JS).
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "runtime", "src"), "*.cs", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(Path.Combine(root, "studio", "engines", "shared"), "*.cs")))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"Create(?:Counter|Histogram|ObservableGauge|UpDownCounter)(?:<\w+>)?\(""(th\.[a-z._]+)"""))
                known.Add(m.Groups[1].Value.Replace('.', '_'));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(root, "studio", "src", "telemetry", "telemetry.ts")), @"create(?:Counter|Histogram|UpDownCounter)\('(th\.[a-z._]+)'"))
            known.Add(m.Groups[1].Value.Replace('.', '_'));
        Assert.Contains("th_payments", known);
        Assert.Contains("th_port_call_duration", known);

        // Every th_ metric a dashboard reads, as Prometheus names it (unit and type suffixes), is one of them.
        var dashboards = Directory.GetFiles(Path.Combine(obs, "grafana", "dashboards"), "*.json");
        Assert.Equal(["engines.json", "payments.json", "ports.json", "store.json"], dashboards.Select(Path.GetFileName).Order().ToArray());
        var unknown = new List<string>();
        foreach (var d in dashboards)
        {
            var json = JsonNode.Parse(File.ReadAllText(d))!;
            // The labels of a grouping (by (...)) are not metrics.
            foreach (var expr in json["panels"]!.AsArray().SelectMany(p => p!["targets"]!.AsArray()).Select(t => System.Text.RegularExpressions.Regex.Replace((string)t!["expr"]!, @"by \([^)]*\)", "")))
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(expr, @"\bth_[a-z_]+(?=\{|\[|\))"))
                {
                    var name = System.Text.RegularExpressions.Regex.Replace(m.Value, "(_bucket|_count|_sum|_total)$", "");
                    name = System.Text.RegularExpressions.Regex.Replace(name, "_seconds$", "");
                    if (!known.Contains(name)) unknown.Add($"{Path.GetFileName(d)}: {m.Value}");
                }
        }
        Assert.True(unknown.Count == 0, "dashboards read metrics no host emits:\n" + string.Join('\n', unknown));
    }

    static byte[] SignedPackage(TestSigners s) => TestSigners.Zip(TestSigners.Sign(new Dictionary<string, string>
    {
        ["workflows/p.wf.corporate.v1@1.0.0.yaml"] = "kind: workflow\n",
        ["manifest.yaml"] = $"kind: export\ntarget: temporal\npromotable: true\nflows:\n  - name: p.wf.corporate.v1\n    kind: workflow\n    wfKind: platform\n    release: 1.0.0\nfiles:\n  - path: workflows/p.wf.corporate.v1@1.0.0.yaml\n    hash: sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("kind: workflow\n"u8.ToArray()))}\n",
    }, s.Key, "p.wf.corporate.v1/temporal@1.0.0"));

    /// <summary>
    /// The alert rules (R-09-72): generated from the agreed targets and current with them, every expression parses, every
    /// metric it reads is one a host defines, and each alert fires on synthetic data where its target is broken and not where it
    /// holds (runtime/observability/slo/test_rules.py); the local stack's Prometheus loads them.
    /// </summary>
    [Fact]
    public void The_alert_rules_are_generated_from_the_targets_parse_and_fire_on_synthetic_data()
    {
        var obs = Path.Combine(Repo(), "runtime", "observability");
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python3", [Path.Combine(obs, "slo", "test_rules.py")]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, output);
        Assert.Contains("all pass", output);
        Assert.StartsWith("# Generated by runtime/observability/slo/build_rules.py", File.ReadAllText(Path.Combine(obs, "alerts", "transacthub.rules.yaml")));
        Assert.Contains("/etc/prometheus/rules/transacthub.rules.yaml", File.ReadAllText(Path.Combine(obs, "prometheus.yml")));
        Assert.Contains("./alerts:/etc/prometheus/rules:ro", File.ReadAllText(Path.Combine(obs, "docker-compose.yml")));
    }

    [Fact]
    public async Task The_release_API_answers_live_and_ready_without_a_token_while_every_other_endpoint_is_401_and_counts_activations()
    {
        var activations = new ConcurrentBag<(long n, string env)>();
        var stored = new ConcurrentBag<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == RuntimeTelemetry.SourceName) l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) =>
        {
            if (i.Name == "th.release.stored") stored.Add(v);
            if (i.Name != "th.release.activations") return;
            foreach (var t in tags) if (t.Key == "th.environment") activations.Add((v, (string)t.Value!));
        });
        listener.Start();

        using var signers = new TestSigners();
        await using var api = new ApiHost(dbs.Fresh(StoreProvider.PostgreSql), signers);
        // No token: the health endpoints answer (an orchestrator's probe carries none), everything else is 401 (R-09-36).
        var anonymous = api.Client(null);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/live")).StatusCode);
        var ready = await anonymous.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains("\"store\":\"healthy\"", await ready.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/releases?scope=p.wf.corporate.v1/temporal&release=1.0.0")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/releases", new ByteArrayContent(SignedPackage(signers)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/activations/active?environment=test&scope=x")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/values?tenant=example-bank")).StatusCode);

        // A stored release and an activation are counted (R-09-34).
        Assert.Equal(HttpStatusCode.Created, (await api.Client("importer").PostAsync("/releases", new ByteArrayContent(SignedPackage(signers)))).StatusCode);
        var act = await api.Client("pipeline").PostAsync("/activations", new StringContent("""{"environment":"test","scope":"p.wf.corporate.v1/temporal","release":"1.0.0"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, act.StatusCode);
        Assert.Contains(1L, stored);
        Assert.Contains(activations, a => a.env == "test" && a.n == 1);

        // Not ready when the store does not answer; live all the same, and still with no token.
        await using var down = new ApiHost(new StoreOptions(StoreProvider.PostgreSql, "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=2"), signers, b => b.UseSetting("Store:Migrate", "false"));
        var dhttp = down.Client(null);
        Assert.Equal(HttpStatusCode.OK, (await dhttp.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await dhttp.GetAsync("/health/ready")).StatusCode);
    }
}
