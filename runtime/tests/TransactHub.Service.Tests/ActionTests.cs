using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Service.Tests;

/// <summary>
/// Troubleshooting actions (R-09-43): each needs a reason; a risky one waits for a ServiceApprover who is another person;
/// those with a path run end to end (a hold released, a dead-letter job requeued, a key rotated, the read model rebuilt);
/// those whose engine path is not built are recorded and refused naming Issue I-101. Every step is a fact.
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
    public async Task A_risky_action_waits_for_another_person_and_one_with_no_engine_path_is_recorded_and_refused()
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

        // Retry has no engine path: recorded, refused, with the issue.
        var retry = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "retry-attempt", target = bulk, reason = "TransientError on submit" }));
        Assert.Equal("refused", (string?)retry["status"]);
        Assert.Equal("ops.NoEnginePath", (string?)retry["code"]);
        Assert.Contains("I-101", (string?)retry["result"]!["message"]);
        foreach (var a in new[] { "redrive-workflow", "reset-activity", "resend-file", "pause-intake", "drain-worker", "restart-worker", "expire-hold" })
        {
            var r = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = a, target = a.EndsWith("intake") ? "uae.wps" : a.EndsWith("worker") ? "th-uae-wps" : bulk, reason = "test" }));
            Assert.Equal("pending-approval", (string?)r["status"]);
            var d = await J.Of(await api.Approver.PostAsJsonAsync($"/service/api/actions/{(string)r["action"]!}/decision", new { approve = true, reason = "test" }));
            Assert.Equal("refused", (string?)d["status"]);
            Assert.Contains("I-101", (string?)d["result"]!["message"]);
        }
        // Re-screen reaches the outbox only (I-99).
        var rs = await J.Of(await se.PostAsJsonAsync("/service/api/actions", new { action = "re-screen", target = bulk, reason = "lists updated" }));
        Assert.Equal("done", (string?)rs["status"]);
        Assert.False((bool)rs["result"]!["delivered"]!);
        await using (var db = StoreDbContext.Open(api.Store))
            Assert.Contains(await db.Outbox.AsNoTracking().Where(o => o.Topic == "engine-signal").Select(o => o.PayloadJson).ToListAsync(), p => p.Contains("re-screen"));
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
