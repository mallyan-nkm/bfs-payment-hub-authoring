using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Service.Tests;

/// <summary>
/// Troubleshooting actions (R-09-43): each needs a reason; a risky one waits for a ServiceApprover who is another person;
/// each runs end to end (a hold released or expired, the operator's controls on the outbox for the engine's host, a file resent,
/// a posting reconciled, intake paused and resumed, a worker drained and restarted, a dead-letter job requeued, a key rotated, the
/// read model rebuilt), Issue I-101. Every step is a fact.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ActionTests(Databases dbs)
{
    sealed class FakeFlowable : HttpMessageHandler
    {
        public readonly List<string> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add($"{r.Method} {r.RequestUri!.AbsolutePath} {await r.Content!.ReadAsStringAsync(ct)} {r.Headers.Authorization?.Scheme}");
            return new HttpResponseMessage(r.RequestUri.AbsolutePath.EndsWith("/job-1") ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task A_risky_action_waits_for_another_person_and_every_troubleshooting_action_reaches_its_target()
    {
        using var s = new Signers();
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub());
        var se = api.Engineer;
        var bulk = await Seed.Bulk(api.Store, "p.wf.corporate.v1");
        await api.CatchUp();

        Assert.Equal(HttpStatusCode.BadRequest, (await se.PostAsJsonAsync("/service/api/actions", new { action = "release-hold", target = bulk })).StatusCode);
        var ask = await se.PostAsJsonAsync("/service/api/actions", new { action = "release-hold", target = bulk, reason = "Limit agreed with the customer (synthetic)" });
        Assert.Equal(HttpStatusCode.Accepted, ask.StatusCode);
        var id = (string)(await J.Of(ask))["action"]!;
        var pending = (await J.Of(await se.GetAsync("/service/api/actions/pending"))).AsArray();
        Assert.Contains(pending, p => (string?)p!["data"]!["action"] == id);
        // The maker may not approve, even holding the approver's role.
        var self = api.As("se-1", "*:ServiceApprover");
        var mine = await self.PostAsJsonAsync($"/service/api/actions/{id}/decision", new { approve = true, reason = "mine" });
        Assert.Equal("ops.MakerApproves", (string?)(await J.Of(mine))["code"]);
        var ok = await J.Of(await api.Approver.PostAsJsonAsync($"/service/api/actions/{id}/decision", new { approve = true, reason = "four eyes" }));
        Assert.Equal("done", (string?)ok["status"]);
        Assert.Equal("approved", (string?)ok["result"]!["decision"]);
        Assert.Equal("ops.AlreadyDecided", (string?)(await J.Of(await api.As("sa-2", "*:ServiceApprover").PostAsJsonAsync($"/service/api/actions/{id}/decision", new { approve = true, reason = "again" })))["code"]);

        // The hold is decided on the payment's chain, as the operations-queue port reads it; the request and its approval are there too.
        await using (var db = StoreDbContext.Open(api.Store))
        {
            var chain = await db.Facts.AsNoTracking().Where(f => f.SubjectId == bulk).OrderBy(f => f.Seq).ToListAsync();
            Assert.Contains(chain, f => f.Kind == "hold" && f.Code == "decided" && f.Actor == "service:sa-1");
            Assert.Equal(["action.requested", "action.approved", "action.done"], chain.Where(f => f.Kind == "service").Select(f => f.Code!));
            Assert.True((await new FactLog(db).Verify(bulk)).Ok);
        }

        // The operator's controls reach the engine's host through the outbox (R-05-55, R-05-57): only a parked step takes them.
        var retry = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "retry-attempt", target = bulk, reason = "TransientError on submit" }));
        Assert.Equal("refused", (string?)retry["status"]);
        Assert.Equal("ops.NoParkedStep", (string?)retry["code"]);
        await using (var db = StoreDbContext.Open(api.Store))
            await new FactLog(db).Append(bulk, "bulk", new NewFact("step", "stuck", new JsonObject { ["instance"] = "root/leg3", ["step"] = "submit" }, Actor: "engine"));
        retry = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "retry-attempt", target = bulk, reason = "TransientError on submit" }));
        Assert.True((string?)retry["status"] == "done", retry.ToJsonString());
        foreach (var a in new[] { "redrive-workflow", "reset-activity" })
        {
            var r = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = a, target = bulk, reason = "the gateway is back" }));
            Assert.Equal("pending-approval", (string?)r["status"]);
            var dd = await J.Of(await api.Approver.PostAsJsonAsync($"/service/api/actions/{(string)r["action"]!}/decision", new { approve = true, reason = "checked" }));
            Assert.True((string?)dd["status"] == "done", dd.ToJsonString());
        }
        // Expire an open hold: recorded expired, and the decision on its way to the engine, which awaits it (R-05-56).
        await using (var db = StoreDbContext.Open(api.Store))
            await new FactLog(db).Append(bulk, "bulk", new NewFact("hold", "opened", new JsonObject { ["hold"] = "hold-2", ["kind"] = "repair", ["activity"] = "p.hold.repair.v1", ["expiry"] = "P1D" }, Actor: "runtime"));
        var ex = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "expire-hold", target = bulk, reason = "the employer withdrew" }));
        Assert.Equal("done", (string?)(await J.Of(await api.Approver.PostAsJsonAsync($"/service/api/actions/{(string)ex["action"]!}/decision", new { approve = true, reason = "checked" })))["status"]);
        // Re-screen is delivered by the engine's host (R-05-57).
        var rs = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "re-screen", target = bulk, reason = "lists updated" }));
        Assert.Equal("done", (string?)rs["status"]);
        Assert.True((bool)rs["result"]!["queued"]!);
        await using (var db = StoreDbContext.Open(api.Store))
        {
            var rows = await db.Outbox.AsNoTracking().Where(o => o.Topic == "engine-signal" && o.Key == bulk).OrderBy(o => o.Id).Select(o => o.PayloadJson).ToListAsync();
            Assert.Equal(["decide", "retry", "redrive", "redrive", "decide", "re-screen"], rows.Select(r => (string?)JsonNode.Parse(r)!["signal"]));
            Assert.True((bool)JsonNode.Parse(rows[3])!["reset"]!);
            Assert.Equal("expired", (string?)JsonNode.Parse(rows[4])!["decision"]);
            Assert.Contains(await db.Facts.AsNoTracking().Where(f => f.SubjectId == bulk && f.Kind == "hold").Select(f => f.Code).ToListAsync(), c => c == "expired");
        }

        // Intake per scheme (R-09-62): paused, the runtime refuses the bank's new payments of the scheme; resumed, it takes them.
        var pause = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "pause-intake", target = "uae.wps", reason = "the Central Bank's window is closed", parameters = new { tenant = Seed.Tenant } }));
        Assert.Equal("done", (string?)(await J.Of(await api.Approver.PostAsJsonAsync($"/service/api/actions/{(string)pause["action"]!}/decision", new { approve = true, reason = "agreed" })))["status"]);
        await using (var db = StoreDbContext.Open(api.Store)) Assert.True(await IntakeSwitch.Paused(db, Seed.Tenant, "uae.wps"));
        Assert.Equal("done", (string?)(await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "resume-intake", target = "uae.wps", reason = "open again", parameters = new { tenant = Seed.Tenant } })))["status"]);
        await using (var db = StoreDbContext.Open(api.Store)) Assert.False(await IntakeSwitch.Paused(db, Seed.Tenant, "uae.wps"));

        // Drain and restart a worker through its own control (R-09-63); one that does not answer is refused with the code.
        using var worker = new FakeWorker();
        foreach (var a in new[] { "drain-worker", "restart-worker" })
        {
            var r = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = a, target = worker.Url, reason = "a deployment of the bank's platform" }));
            var d = await J.Of(await api.Approver.PostAsJsonAsync($"/service/api/actions/{(string)r["action"]!}/decision", new { approve = true, reason = "agreed" }));
            Assert.True((string?)d["status"] == "done", d.ToJsonString());
        }
        Assert.Equal(["/drain", "/restart"], worker.Calls);
        var gone = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "drain-worker", target = "http://127.0.0.1:9", reason = "test" }));
        Assert.Equal("ops.WorkerUnreachable", (string?)(await J.Of(await api.Approver.PostAsJsonAsync($"/service/api/actions/{(string)gone["action"]!}/decision", new { approve = true, reason = "test" })))["code"]);
    }

    /// <summary>A worker's control endpoints, as the worker answers them (POST /drain, POST /restart).</summary>
    sealed class FakeWorker : IDisposable
    {
        readonly HttpListener listener;
        public readonly List<string> Calls = new();
        public string Url { get; }

        public FakeWorker()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            Url = $"http://127.0.0.1:{port}";
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await listener.GetContextAsync(); } catch { return; }
                    lock (Calls) Calls.Add(c.Request.Url!.AbsolutePath);
                    var body = System.Text.Encoding.UTF8.GetBytes($"{{\"draining\":true,\"restarting\":{(c.Request.Url!.AbsolutePath == "/restart" ? "true" : "false")}}}");
                    c.Response.ContentType = "application/json";
                    await c.Response.OutputStream.WriteAsync(body);
                    c.Response.Close();
                }
            });
        }

        public void Dispose() => listener.Close();
    }

    [Fact]
    public async Task A_file_is_resent_as_kept_and_an_unknown_posting_is_reconciled_with_core_banking()
    {
        using var s = new Signers();
        var outFolder = Directory.CreateTempSubdirectory("th-resend-").FullName;
        try
        {
            await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub(), new() { ["TH_FILE_TRANSFER_DIR"] = outFolder });
            _ = api.Engineer; // the host starts and migrates the store
            var bulk = await Seed.Bulk(api.Store, "p.wf.corporate.v1");
            var sif = System.Text.Encoding.ASCII.GetBytes("EDR,synthetic\r\nSCR,synthetic\r\n");
            await using (var db = StoreDbContext.Open(api.Store))
                await new PaymentStore(db).KeepMessage(Seed.Tenant, "uae.wps", TransactHub.Model.Direction.Outward, "uae.wps.sif", "0000001234567202611011200000.SIF", sif, bulk);
            await api.CatchUp();
            // Resend (R-05-58): the same bytes on the file-transfer port, once per file id; a second resend leaves the receiver's file.
            for (var n = 0; n < 2; n++)
            {
                var r = await J.Of(await api.Engineer.PostAsJsonAsync("/service/api/actions", new { action = "resend-file", target = bulk, reason = "the Central Bank asked for it again" }));
                var d = await J.Of(await api.Approver.PostAsJsonAsync($"/service/api/actions/{(string)r["action"]!}/decision", new { approve = true, reason = "checked" }));
                Assert.Equal("done", (string?)d["status"]);
                Assert.Equal(n == 0 ? "sent" : "already there", (string?)d["result"]!["result"]);
            }
            Assert.Equal(sif, await File.ReadAllBytesAsync(Path.Combine(outFolder, "0000001234567202611011200000.SIF")));
            Assert.Single(Directory.GetFiles(outFolder));
            await using (var db = StoreDbContext.Open(api.Store))
                Assert.Equal(2, await db.Facts.AsNoTracking().CountAsync(f => f.SubjectId == bulk && f.Kind == "message" && f.Code == "resent"));

            // Reconcile (R-08-19): core banking is asked by the posting's reference; posted, the parked step is re-driven; not posted, retried.
            var cb = new TransactHub.Ports.SimulatedCoreBanking(new TransactHub.Ports.PortBinding(Seed.Tenant, "p.port.core-banking.v1", "simulator", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), new Dictionary<string, string>()));
            await cb.Post(new TransactHub.Ports.PostingBatch("POST-1", new DateOnly(2026, 11, 17), []));
            var signals = new TransactHub.Operations.OutboxSignals(api.Store);
            var reconcile = ActionBook.ReconcilePosting(api.Store, signals, _ => cb, TimeProvider.System);
            await using (var db = StoreDbContext.Open(api.Store))
                await new FactLog(db).Append(bulk, "bulk", new NewFact("step", "stuck", new JsonObject { ["instance"] = "root", ["step"] = "post" }, Actor: "engine"));
            var who = new ServiceIdentity("se-1", "se-1", new HashSet<string> { ServiceRoles.Engineer }, new HashSet<string> { "*" });
            var posted = await reconcile(who, new ActionAsk("reconcile-posting", bulk, "the answer was lost", new JsonObject { ["reference"] = "POST-1" }), default);
            Assert.True((bool)posted["posted"]!);
            Assert.Equal("redrive", (string?)posted["then"]);
            var notPosted = await reconcile(who, new ActionAsk("reconcile-posting", bulk, "the answer was lost", new JsonObject { ["reference"] = "POST-2" }), default);
            Assert.False((bool)notPosted["posted"]!);
            Assert.Equal("retry", (string?)notPosted["then"]);
            await using (var db = StoreDbContext.Open(api.Store))
            {
                Assert.Equal(2, await db.Facts.AsNoTracking().CountAsync(f => f.SubjectId == bulk && f.Kind == "posting" && f.Code == "reconciled"));
                var rows = await db.Outbox.AsNoTracking().Where(o => o.Topic == "engine-signal" && o.Key == bulk).OrderBy(o => o.Id).Select(o => o.PayloadJson).ToListAsync();
                Assert.Equal(["redrive", "retry"], rows.Select(r => (string?)JsonNode.Parse(r)!["signal"]));
            }
        }
        finally { Directory.Delete(outFolder, true); }
    }

    [Fact]
    public async Task A_dead_letter_job_is_requeued_a_key_rotated_and_the_read_model_rebuilt()
    {
        using var s = new Signers();
        var flowable = new FakeFlowable();
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub(), new()
        {
            ["Service:Flowable:Url"] = "http://flowable.example.test/flowable-rest", ["Service:Flowable:PasswordRef"] = "env:TH_TEST_FLOWABLE_PASSWORD", ["TH_TEST_FLOWABLE_PASSWORD"] = "synthetic-test-only",
            ["Service:TrustWritable"] = "true",
        }, flowable: flowable);
        var se = api.Engineer;
        var r = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "requeue-dead-letter", target = "job-1", reason = "core banking back" }));
        Assert.Equal("done", (string?)r["status"]);
        Assert.Equal("POST /flowable-rest/service/management/deadletter-jobs/job-1 {\"action\":\"move\"} Basic", flowable.Calls.Single());
        var missing = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "requeue-dead-letter", target = "job-9", reason = "x" }));
        Assert.Equal("flowable.DeadLetterJob", (string?)missing["code"]);

        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ask = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "rotate-trusted-key", target = s.KeyId, reason = "studio key rotation", parameters = new { publicKey = next.ExportSubjectPublicKeyInfoPem() } }));
        var rot = await J.Of(await api.Approver.PostAsJsonAsync($"/service/api/actions/{(string)ask["action"]!}/decision", new { approve = true, reason = "planned" }));
        Assert.Equal("done", (string?)rot["status"]);
        var trust = JsonNode.Parse(File.ReadAllText(s.TrustFile))!;
        Assert.Contains(trust["keys"]!.AsArray(), k => (string?)k!["keyId"] == TransactHub.Security.ReleaseSignature.KeyIdOf(next));
        Assert.NotNull(trust["keys"]!.AsArray().Single(k => (string?)k!["keyId"] == s.KeyId)!["retiredAt"]);

        var rb = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "rebuild-read-model", reason = "after a restore" }));
        var built = await J.Of(await api.Approver.PostAsJsonAsync($"/service/api/actions/{(string)rb["action"]!}/decision", new { approve = true, reason = "ok" }));
        Assert.True((bool)built["result"]!["rebuilt"]!);
        var replay = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "replay-read-model", reason = "catch up" }));
        Assert.Equal(0, (int)replay["result"]!["behind"]!);
    }
}
