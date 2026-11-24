using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Engines;
using TransactHub.Engines.Durable;
using TransactHub.Store;

namespace TransactHub.EngineHost.Tests;

/// <summary>
/// Issue I-165, R-09-196 to R-09-199: on DTFx, Elsa and Flowable one engine service per distribution serves all its payments and
/// survives a restart of its host. A payment starts, the host stops while the payment waits (DTFx and Elsa for a message, Flowable on
/// a timer), a new host starts, the message is delivered, and the payment ends with its facts chains intact: the payment's own, its
/// payload's and the service's history (DTFx, Elsa) or the binding's journal (Flowable). On both databases.
/// </summary>
public partial class EngineHostTests
{
    async Task UseStore(string database) => store = database == "sqlserver" ? await servers.SqlServer() : servers.Store;

    /// <summary>A bank's distribution installed and active, with its plan: from the stored package (DTFx, Elsa) or written (Flowable's BPMN).</summary>
    async Task<(string bank, string dist, string plans, string workflow)> Distribution(string engine, string prefix, string wait, Worker worker)
    {
        var bank = $"{prefix}-{engine}-{Guid.NewGuid():N}"[..20];
        const string id = "2026.11.24-1";
        var dist = DistributionRegistry.IdOf(bank, id);
        var plans = Path.Combine(Path.GetTempPath(), $"th-hosts-{bank}");
        Directory.CreateDirectory(plans);
        string workflow;
        if (engine == "flowable")
        {
            await Install(bank, id);
            await File.WriteAllTextAsync(Path.Combine(plans, $"{Safe.Of(dist)}.json"), Plan(engine, "1.0.0", worker.Url, wait).ToJsonString());
            workflow = "p.wf.pay.v1";
        }
        else
        {
            var package = Package("1.0.0");
            await WithRegistry(r => r.Store(bank, id, package, new JsonObject { ["id"] = id, ["bank"] = bank }, Convert.ToHexStringLower(SHA256.HashData(package)), "test", "e2e"));
            System.Environment.SetEnvironmentVariable("TH_HOST_WORKERS", new JsonObject { ["th-p"] = worker.Url }.ToJsonString());
            workflow = "p.wf.ask.v1";
        }
        await WithRegistry(r => r.Activate(dist, Environment, "test", "e2e"));
        return (bank, dist, plans, workflow);
    }

    async Task<Supervisor> Current(string engine, string bank, string plans, string dist)
    {
        var sup = await StartSupervisor(engine, bank, plans);
        await Until(async () => J.Truthy(J.P(J.P(await sup.Distribution(dist), "host"), "current")), TimeSpan.FromMinutes(3), $"{dist} current");
        return sup;
    }

    async Task<JsonObject> Ended(Supervisor sup, string p)
    {
        JsonObject? e = null;
        await Until(async () => J.S(e = (await sup.Call("GET", $"/payments/{p}")).body, "status") is "ended" or "failed", TimeSpan.FromMinutes(4), $"{p} ended");
        return e!;
    }

    /// <summary>Every chain of a payment checked: its own subject's and each of its payloads'.</summary>
    async Task ChainsIntact(string paymentId)
    {
        var subject = await SubjectOf(paymentId) ?? throw new Xunit.Sdk.XunitException($"{paymentId} was not accepted");
        await using var db = StoreDbContext.Open(Db);
        var facts = new FactLog(db);
        var own = await facts.Verify(subject);
        Assert.True(own.Ok, $"{paymentId}'s chain: {own}");
        Assert.True((await facts.Verify(paymentId)).Ok, $"{paymentId}'s pin chain");
        var refs = await db.Facts.AsNoTracking().Where(f => f.SubjectKind == "payload" && f.DataJson.Contains(paymentId)).Select(f => f.SubjectId).Distinct().ToListAsync();
        Assert.NotEmpty(refs);
        foreach (var r in refs) Assert.True((await facts.Verify(r)).Ok, $"payload chain {r}");
        // The payment ended once: one end recorded, though its host stopped while it ran.
        Assert.Equal(1, await db.Facts.AsNoTracking().CountAsync(f => f.SubjectId == paymentId && f.Kind == DistributionRegistry.EndKind));
    }

    /// <summary>The chain the engine keeps for the distribution: the service's history (DTFx, Elsa), the binding's journal (Flowable).</summary>
    async Task<List<FactRow>> EngineChain(string engine, string dist)
    {
        await using var db = StoreDbContext.Open(Db);
        var subject = engine == "flowable"
            ? $"flowable-run:dist-{EngineHistory.Key(dist)}"
            : StoreHistory.SubjectOf(engine, dist);
        var v = await new FactLog(db).Verify(subject);
        Assert.True(v.Ok && v.Facts > 0, $"{subject}: {v}");
        return await new FactLog(db).Of(subject);
    }

    async Task Restart(string engine, string database)
    {
        await UseStore(database);
        using var worker = new Worker();
        var (bank, dist, plans, workflow) = await Distribution(engine, "rs", "PT25S", worker);
        var sup = await Current(engine, bank, plans, dist);
        var log = "";
        try
        {
            var p = $"R1-{bank}";
            var r = await sup.Call("POST", "/payments", new JsonObject { ["paymentId"] = p, ["context"] = Context(p), ["workflow"] = workflow });
            Assert.True(r.status == 202, r.body.ToJsonString());
            // Its first step is done: it now waits, for the message (DTFx, Elsa) or on its timer (Flowable).
            await Until(() => StepRecorded(p, "ok"), TimeSpan.FromMinutes(3), $"{p}'s first step");
            await Task.Delay(TimeSpan.FromSeconds(1));
            // The host stops mid-run, as a machine that fails does: the supervisor, its hosts and their engine services are killed.
            log += sup.Log;
            await sup.DisposeAsync();
            await Task.Delay(TimeSpan.FromSeconds(2));

            sup = await Current(engine, bank, plans, dist);
            // The new host took the payment back from the engine's record.
            await Until(async () => (await sup.Call("GET", $"/payments/{p}")).status == 200, TimeSpan.FromMinutes(2), $"{p} taken back");
            Assert.True(J.Truthy(J.P((await sup.Call("GET", $"/payments/{p}")).body, "resumed")));
            if (engine != "flowable")
            {
                (int status, JsonObject body) m = default;
                await Until(async () => (m = await sup.Call("POST", $"/payments/{p}/messages", new JsonObject { ["message"] = new JsonObject { ["type"] = "p.test.answer", ["id"] = $"answer-{p}" } })).status == 202, TimeSpan.FromMinutes(2), "the message taken");
            }
            var e = await Ended(sup, p);
            Assert.True(J.S(e, "outcome") == "done", e.ToJsonString());
            var trace = J.Strings(J.P(J.A(J.P(e, "observation"), "instances")[0], "trace"));
            if (engine != "flowable") Assert.Contains("w:received", trace);
            Assert.Contains($"{p}:b@1.0.0", worker.Calls);
            await ChainsIntact(p);
            var chain = await EngineChain(engine, dist);
            if (engine != "flowable")
            {
                // The service rebuilt itself from its history once: a fact of the restart, and the payment's start once.
                Assert.Contains(chain, f => f.Code == "restarted");
                Assert.Single(chain, f => f.Code == "start");
                Assert.Contains(chain, f => f.Code == "reported" && f.DataJson.Contains(p));
            }
            // Insert-only: every fact the store holds for the run is still there, in its chain.
            await using var db = StoreDbContext.Open(Db);
            Assert.True(await db.Facts.AsNoTracking().AnyAsync(f => f.Kind == "engine" && f.Code == "started" && f.DataJson.Contains(engine)));
        }
        catch (Exception x) when (x is not Xunit.Sdk.XunitException || !x.Message.Contains("--- the supervisor's log ---"))
        {
            throw new Xunit.Sdk.XunitException($"{x.Message}\n{x.StackTrace}\n--- the supervisor's log ---\n{log}\n--- after the restart ---\n{sup.Log}");
        }
        finally
        {
            await sup.DisposeAsync();
            System.Environment.SetEnvironmentVariable("TH_HOST_WORKERS", null);
            try { Directory.Delete(plans, true); } catch { }
        }
    }

    /// <summary>Many payments of one distribution on one service: one process, one history (DTFx, Elsa) or one deployment (Flowable).</summary>
    async Task OneService(string engine)
    {
        await UseStore("postgres");
        using var worker = new Worker();
        var (bank, dist, plans, workflow) = await Distribution(engine, "one", "PT1S", worker);
        await using var sup = await Current(engine, bank, plans, dist);
        try
        {
            var ids = Enumerable.Range(1, 4).Select(n => $"S{n}-{bank}").ToList();
            foreach (var p in ids)
            {
                var r = await sup.Call("POST", "/payments", new JsonObject { ["paymentId"] = p, ["context"] = Context(p), ["workflow"] = workflow });
                Assert.True(r.status == 202, r.body.ToJsonString());
            }
            if (engine != "flowable")
                foreach (var p in ids)
                {
                    await Until(() => StepRecorded(p, "ok"), TimeSpan.FromMinutes(3), $"{p}'s first step");
                    await Until(async () => (await sup.Call("POST", $"/payments/{p}/messages", new JsonObject { ["message"] = new JsonObject { ["type"] = "p.test.answer", ["id"] = $"answer-{p}" } })).status == 202, TimeSpan.FromMinutes(2), "the message taken");
                }
            var ends = new List<JsonObject>();
            foreach (var p in ids) ends.Add(await Ended(sup, p));
            Assert.All(ends, e => Assert.True(J.S(e, "outcome") == "done", e.ToJsonString()));
            var engines = ends.Select(e => J.P(J.P(e, "observation"), "engine")).ToList();
            if (engine == "flowable")
            {
                // One deployment of the distribution, in its tenant, served every payment.
                Assert.Single(engines.Select(x => J.S(x, "deployment")).Distinct());
                Assert.Single(engines.Select(x => J.S(x, "tenant")).Distinct());
            }
            else
            {
                Assert.Single(engines.Select(x => J.S(J.P(x, "service"), "pid")).Distinct());
                var chain = await EngineChain(engine, dist);
                Assert.Equal(ids.Count, chain.Count(f => f.Code == "start"));
                Assert.Single(chain, f => f.Code == "opened");
            }
            var status = J.P(await sup.Distribution(dist), "engine");
            Assert.NotNull(J.P(J.P(status, "service"), "pid"));
        }
        catch (Exception x) when (x is not Xunit.Sdk.XunitException || !x.Message.Contains("--- the supervisor's log ---"))
        {
            throw new Xunit.Sdk.XunitException($"{x.Message}\n{x.StackTrace}\n--- the supervisor's log ---\n{sup.Log}");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("TH_HOST_WORKERS", null);
            try { Directory.Delete(plans, true); } catch { }
        }
    }

    [Fact] public Task Dtfx_survives_a_restart_of_its_host_on_PostgreSql() => Restart("dtfx", "postgres");
    [Fact] public Task Dtfx_survives_a_restart_of_its_host_on_SqlServer() => Restart("dtfx", "sqlserver");
    [Fact] public Task Elsa_survives_a_restart_of_its_host_on_PostgreSql() => Restart("elsa", "postgres");
    [Fact] public Task Elsa_survives_a_restart_of_its_host_on_SqlServer() => Restart("elsa", "sqlserver");
    [Fact] public Task Flowable_survives_a_restart_of_its_host_on_PostgreSql() => Restart("flowable", "postgres");
    [Fact] public Task Flowable_survives_a_restart_of_its_host_on_SqlServer() => Restart("flowable", "sqlserver");

    [Fact] public Task Dtfx_serves_many_payments_of_a_distribution_on_one_service() => OneService("dtfx");
    [Fact] public Task Elsa_serves_many_payments_of_a_distribution_on_one_service() => OneService("elsa");
    [Fact] public Task Flowable_serves_many_payments_of_a_distribution_from_one_deployment() => OneService("flowable");
}
