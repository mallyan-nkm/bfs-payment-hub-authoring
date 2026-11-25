using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Engines;
using TransactHub.Engines.Durable;
using TransactHub.Store;

namespace TransactHub.EngineHost.Tests;

/// <summary>
/// Issue I-165, R-09-196 to R-09-199: on DTFx, Elsa and Flowable one engine service per distribution serves all its payments and
/// survives a restart of its host. A payment starts, the host stops while the payment waits for a message, a new host starts, the message is delivered, and the payment ends with its facts chains intact: the payment's own, its
/// payload's and the service's history (DTFx, Elsa) or the binding's journal (Flowable). On both databases.
/// </summary>
public partial class EngineHostTests
{
    /// <summary>
    /// On Flowable, the payment of <c>p.wf.ask.v1</c> as the generator writes it for a REST binding: a step, an await of
    /// <c>p.test.answer</c> (an event-based gateway, the message or a three-minute timeout), a step, the end.
    /// </summary>
    static string AskBpmn(string release)
    {
        var key = $"p_wf_ask_v1_{release.Replace('.', '_')}";
        string Call(string s) => $$"""
            <serviceTask id="{{s}}_task" flowable:type="external-worker" flowable:topic="th-p:p.enrich.check.v1@{{release}}" flowable:exclusive="false">
              <extensionElements>
                <flowable:executionListener event="start" expression="${execution.setVariable('th_attempt', 1)}"/>
                <th:task step="{{s}}" activity="p.enrich.check.v1" release="{{release}}" record="business"/>
              </extensionElements>
            </serviceTask>
            <serviceTask id="{{s}}_rec" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="call" step="{{s}}" var="outcome_{{s}}" record="business"/></extensionElements></serviceTask>
            """;
        string Point(string id, string attrs) => $$"""<serviceTask id="{{id}}" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point {{attrs}}/></extensionElements></serviceTask>""";
        return $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:flowable="http://flowable.org/bpmn" xmlns:th="urn:transacthub:flowable:1" targetNamespace="urn:test">
          <message id="msg_answer" name="p.test.answer"/>
          <process id="{{key}}" isExecutable="true">
            <extensionElements><th:workflow name="p.wf.ask.v1" release="{{release}}" kind="workflow"/></extensionElements>
            <startEvent id="start"/>
            {{Point("th_started", "kind=\"processStart\"")}}
            {{Call("a")}}
            <eventBasedGateway id="w" name="await w"/>
            <intermediateCatchEvent id="w_message" name="w:received"><extensionElements><th:await step="w" message="p.test.answer"/></extensionElements><messageEventDefinition messageRef="msg_answer"/></intermediateCatchEvent>
            {{Point("w_got", "kind=\"awaitReceived\" step=\"w\" message=\"p.test.answer\" record=\"business\"")}}
            <intermediateCatchEvent id="w_timeout" name="w:timeout"><timerEventDefinition><timeDuration>PT3M</timeDuration></timerEventDefinition></intermediateCatchEvent>
            {{Point("w_out", "kind=\"awaitTimeout\" step=\"w\" message=\"p.test.answer\" record=\"business\"")}}
            {{Call("b")}}
            <intermediateThrowEvent id="th_endpoint_done"><extensionElements><flowable:executionListener event="start" expression="${execution.setVariable('th_outcome', 'done')}"/></extensionElements></intermediateThrowEvent>
            <intermediateThrowEvent id="th_endpoint_timedout"><extensionElements><flowable:executionListener event="start" expression="${execution.setVariable('th_outcome', 'timedout')}"/></extensionElements></intermediateThrowEvent>
            {{Point("th_end", "kind=\"end\"")}}
            <endEvent id="end"/>
            <sequenceFlow id="f1" sourceRef="start" targetRef="th_started"/>
            <sequenceFlow id="f2" sourceRef="th_started" targetRef="a_task"/>
            <sequenceFlow id="f3" sourceRef="a_task" targetRef="a_rec"/>
            <sequenceFlow id="f4" sourceRef="a_rec" targetRef="w"/>
            <sequenceFlow id="f5" sourceRef="w" targetRef="w_message"/>
            <sequenceFlow id="f6" sourceRef="w_message" targetRef="w_got"/>
            <sequenceFlow id="f7" sourceRef="w_got" targetRef="b_task"/>
            <sequenceFlow id="f8" sourceRef="w" targetRef="w_timeout"/>
            <sequenceFlow id="f9" sourceRef="w_timeout" targetRef="w_out"/>
            <sequenceFlow id="f10" sourceRef="w_out" targetRef="th_endpoint_timedout"/>
            <sequenceFlow id="f11" sourceRef="th_endpoint_timedout" targetRef="th_end"/>
            <sequenceFlow id="f12" sourceRef="b_task" targetRef="b_rec"/>
            <sequenceFlow id="f13" sourceRef="b_rec" targetRef="th_endpoint_done"/>
            <sequenceFlow id="f14" sourceRef="th_endpoint_done" targetRef="th_end"/>
            <sequenceFlow id="f15" sourceRef="th_end" targetRef="end"/>
          </process>
        </definitions>
        """;
    }

    static JsonObject AskPlan(string release, string workerUrl)
    {
        var plan = Plan("flowable", release, workerUrl, "PT1S");
        var docs = (JsonObject)plan["documents"]!;
        docs[$"p.wf.ask.v1@{release}"] = JsonNode.Parse($$"""
            { "kind": "workflow", "wfKind": "platform", "name": "p.wf.ask.v1", "release": "{{release}}", "do": [
              { "id": "a", "call": "p.enrich.check.v1" },
              { "id": "w", "await": { "message": "p.test.answer", "timeout": "PT3M", "onTimeout": { "end": "timedout" } } },
              { "id": "b", "call": "p.enrich.check.v1" },
              { "end": "done" } ] }
            """);
        ((JsonObject)plan["built"]!)["workflow"] = "p.wf.ask.v1";
        plan["bpmn"] = new JsonObject { [$"p.wf.ask.v1@{release}.bpmn20.xml"] = AskBpmn(release) };
        return plan;
    }

    /// <summary>On Flowable, <c>p.wf.timeout.v1</c> as the studio exports it (<see cref="TimeoutBpmn"/>): a step whose attempt may outlive its three seconds.</summary>
    static JsonObject TimeoutPlan(string release, string workerUrl)
    {
        var plan = Plan("flowable", release, workerUrl, "PT1S");
        var docs = (JsonObject)plan["documents"]!;
        docs[$"p.wf.timeout.v1@{release}"] = JsonNode.Parse($$"""
            { "kind": "workflow", "wfKind": "platform", "name": "p.wf.timeout.v1", "release": "{{release}}", "do": [
              { "id": "a", "call": "p.enrich.check.v1", "policy": { "attemptTimeout": "PT3S" } },
              { "id": "b", "call": "p.enrich.check.v1" },
              { "end": "done" } ] }
            """);
        ((JsonObject)plan["built"]!)["workflow"] = "p.wf.timeout.v1";
        plan["bpmn"] = new JsonObject { [$"p.wf.timeout.v1@{release}.bpmn20.xml"] = TimeoutBpmn.Xml };
        return plan;
    }

    async Task UseStore(string database) => store = database == "sqlserver" ? await servers.SqlServer() : servers.Store;

    /// <summary>A bank's distribution installed and active, with its plan: from the stored package (DTFx, Elsa) or written (Flowable's BPMN).</summary>
    async Task<(string bank, string dist, string plans, string workflow)> Distribution(string engine, string prefix, string wait, Worker worker, bool timeout = false)
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
            await File.WriteAllTextAsync(Path.Combine(plans, $"{Safe.Of(dist)}.json"), (timeout ? TimeoutPlan("1.0.0", worker.Url) : AskPlan("1.0.0", worker.Url)).ToJsonString());
            workflow = timeout ? "p.wf.timeout.v1" : "p.wf.ask.v1";
        }
        else
        {
            var package = Package("1.0.0", timeout: timeout);
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
            // Its first step is done: it now waits for the message.
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
            {
                (int status, JsonObject body) m = default;
                await Until(async () => (m = await sup.Call("POST", $"/payments/{p}/messages", new JsonObject { ["message"] = new JsonObject { ["type"] = "p.test.answer", ["id"] = $"answer-{p}" } })).status == 202, TimeSpan.FromMinutes(2), "the message taken");
            }
            var e = await Ended(sup, p);
            Assert.True(J.S(e, "outcome") == "done", e.ToJsonString());
            var trace = J.Strings(J.P(J.A(J.P(e, "observation"), "instances")[0], "trace"));
            Assert.Contains("w:received", trace);
            if (engine == "flowable") Assert.True(J.Num(J.P(J.P(e, "observation"), "engine"), "restarts") >= 1, "the binding restarted from its journal");
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
    [Fact] public Task Dtfx_runs_a_distributions_payments_side_by_side_while_one_workers_call_is_slow() => SideBySide("dtfx");
    [Fact] public Task Elsa_runs_a_distributions_payments_side_by_side_while_one_workers_call_is_slow() => SideBySide("elsa");
    [Fact] public Task Elsa_runs_a_distributions_payments_side_by_side_while_one_workers_call_is_slow_on_SqlServer() => SideBySide("elsa", "sqlserver");

    /// <summary>
    /// R-09-238 (Issue I-300): a distribution's DTFx service runs several work items at once, as many as its host's setting allows
    /// (TH_HOST_SERVICE_CONCURRENCY, default 4): a payment whose worker call is slow holds up no other payment of the distribution.
    /// With one work item at once, the second payment's first step would wait for the first's slow call. R-09-286 (Issue I-360):
    /// Elsa's service runs its attempts so too, off its clock's thread.
    /// </summary>
    async Task SideBySide(string engine, string database = "postgres")
    {
        await UseStore(database);
        using var worker = new Worker();
        var (bank, dist, plans, workflow) = await Distribution(engine, "sbs", "PT1M", worker);
        var slow = $"S1-{bank}";
        worker.Delay = (payment, step) => payment == slow && step == "a" ? TimeSpan.FromSeconds(25) : TimeSpan.Zero;
        await using var sup = await Current(engine, bank, plans, dist);
        try
        {
            var fast = $"S2-{bank}";
            foreach (var p in new[] { slow, fast })
            {
                var r = await sup.Call("POST", "/payments", new JsonObject { ["paymentId"] = p, ["context"] = Context(p), ["workflow"] = workflow });
                Assert.True(r.status == 202, r.body.ToJsonString());
            }
            var t0 = DateTimeOffset.UtcNow;
            await Until(() => StepRecorded(fast, "ok"), TimeSpan.FromSeconds(20), $"{fast}'s first step while {slow}'s call is slow");
            Assert.False(await StepRecorded(slow, "ok"), $"{slow}'s slow call answered within {DateTimeOffset.UtcNow - t0}");
            await Until(() => StepRecorded(slow, "ok"), TimeSpan.FromMinutes(2), $"{slow}'s first step");
            foreach (var p in new[] { slow, fast })
                await Until(async () => (await sup.Call("POST", $"/payments/{p}/messages", new JsonObject { ["message"] = new JsonObject { ["type"] = "p.test.answer", ["id"] = $"answer-{p}" } })).status == 202, TimeSpan.FromMinutes(2), "the message taken");
            foreach (var p in new[] { slow, fast })
            {
                var e = await Ended(sup, p);
                Assert.True(J.S(e, "outcome") == "done", e.ToJsonString());
                Assert.Equal(4, (int?)J.Num(J.P(J.P(J.P(e, "observation"), "engine"), "service"), "concurrency"));
            }
            await EngineChain(engine, dist);
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
    [Fact] public Task Dtfx_abandons_an_attempt_at_its_deadline_and_retries_it_on_PostgreSql() => OutlivesItsTimeout("dtfx", "postgres");
    [Fact] public Task Dtfx_abandons_an_attempt_at_its_deadline_and_retries_it_on_SqlServer() => OutlivesItsTimeout("dtfx", "sqlserver");
    [Fact] public Task Elsa_abandons_an_attempt_at_its_deadline_and_retries_it_on_PostgreSql() => OutlivesItsTimeout("elsa", "postgres");
    [Fact] public Task Elsa_abandons_an_attempt_at_its_deadline_and_retries_it_on_SqlServer() => OutlivesItsTimeout("elsa", "sqlserver");

    /// <summary>
    /// R-09-298, R-09-300 (Issue I-390, the owner's decision of 24 November 2026: the attempt timeout is the engine's). On the engine
    /// host a worker's call that outlives its attempt's timeout (three seconds; the call takes ten) is abandoned at its deadline on the
    /// host's clock, and the retry runs while the first call is still out, though the service runs one work item at a time
    /// (TH_HOST_SERVICE_CONCURRENCY=1). The first call's late answer is ignored and recorded on the service's history, never on the
    /// payment: the payment's facts hold the attempt that timed out and the one that answered, and its step's fact is the retry's.
    /// </summary>
    async Task OutlivesItsTimeout(string engine, string database)
    {
        await UseStore(database);
        using var worker = new Worker { TagAttempts = true };
        var (bank, dist, plans, _) = await Distribution(engine, "to", "PT1S", worker, timeout: true);
        var p = $"T1-{bank}";
        var slow = TimeSpan.FromSeconds(10);
        worker.AttemptDelay = (payment, step, attempt) => payment == p && step == "a" && attempt == 1 ? slow : TimeSpan.Zero;
        System.Environment.SetEnvironmentVariable("TH_HOST_SERVICE_CONCURRENCY", "1");
        Supervisor? sup = null;
        try
        {
            sup = await Current(engine, bank, plans, dist);
            var r = await sup.Call("POST", "/payments", new JsonObject { ["paymentId"] = p, ["context"] = Context(p), ["workflow"] = "p.wf.timeout.v1" });
            Assert.True(r.status == 202, r.body.ToJsonString());
            var e = await Ended(sup, p);
            Assert.True(J.S(e, "outcome") == "done", e.ToJsonString());
            // The retry reached the worker after the deadline and before the first call answered: the first was abandoned, not awaited.
            await Until(() => Task.FromResult(worker.Timed.Any(x => x.call == $"{p}:a" && x.attempt == 1)), TimeSpan.FromSeconds(30), "the first call's late answer");
            var a1 = worker.Timed.First(x => x.call == $"{p}:a" && x.attempt == 1);
            var a2 = worker.Timed.First(x => x.call == $"{p}:a" && x.attempt == 2);
            Assert.True(a2.at - a1.at >= TimeSpan.FromSeconds(2.5), $"the retry came {a2.at - a1.at} after the first attempt: before its deadline");
            Assert.True(a2.at < a1.answered, $"the retry came at {a2.at:O}, after the first call answered at {a1.answered:O}: the attempt was not abandoned at its deadline");
            Assert.Equal(1, worker.Timed.Count(x => x.call == $"{p}:b"));
            // The payment took the retry's answer, never the late one.
            var ctx = J.P(J.A(J.P(e, "observation"), "instances").FirstOrDefault(), "ctx");
            if (ctx is not null) Assert.Equal("1.0.0#2", J.S(J.P(ctx, "facts"), "a"));
            // The late answer is on the service's history, ignored (R-09-298).
            await Until(async () => (await EngineChain(engine, dist)).Any(f => f.Code == "late-answer"), TimeSpan.FromSeconds(30), "the late answer on the history");
            var late = (await EngineChain(engine, dist)).Single(f => f.Code == "late-answer");
            var lf = JsonNode.Parse(late.DataJson)!;
            Assert.Equal(("a", 1, "result", "ok"), (J.S(lf, "step"), (int?)J.Num(lf, "attempt"), J.S(lf, "kind"), J.S(lf, "outcome")));
            Assert.DoesNotContain("1.0.0#1", late.DataJson);
            // The payment's facts: step a timed out on its first attempt and answered on its second; nothing of the late answer.
            var subject = await SubjectOf(p) ?? throw new Xunit.Sdk.XunitException($"{p} was not accepted");
            await using (var db = StoreDbContext.Open(Db))
            {
                var steps = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subject && f.Kind == "step").OrderBy(f => f.Seq).ToListAsync();
                var ofA = steps.Select(f => (f.Code, d: JsonNode.Parse(f.DataJson)!)).Where(x => J.S(x.d, "step") == "a").ToList();
                Assert.Equal(new[] { "error", "ok" }, ofA.Select(x => x.Code).ToArray());
                Assert.Equal("Timeout", J.S(J.P(ofA[0].d, "error"), "name"));
                Assert.Equal(new[] { 1, 2 }, ofA.Select(x => (int)(J.Num(x.d, "attempt") ?? 0)).ToArray());
                Assert.DoesNotContain(ofA, x => x.d.ToJsonString().Contains("1.0.0#1"));
                Assert.Equal("1.0.0#2", (string?)ofA[1].d["writes"]?["facts.a"]);
            }
            await ChainsIntact(p);
        }
        catch (Exception x) when (x is not Xunit.Sdk.XunitException || !x.Message.Contains("--- the supervisor's log ---"))
        {
            throw new Xunit.Sdk.XunitException($"{x.Message}\n{x.StackTrace}\n--- the supervisor's log ---\n{sup?.Log}");
        }
        finally
        {
            if (sup is not null) await sup.DisposeAsync();
            System.Environment.SetEnvironmentVariable("TH_HOST_SERVICE_CONCURRENCY", null);
            System.Environment.SetEnvironmentVariable("TH_HOST_WORKERS", null);
            try { Directory.Delete(plans, true); } catch { }
        }
    }

    [Fact] public Task Temporal_abandons_an_attempt_at_its_deadline_and_retries_it_on_PostgreSql() => BindingPlacesTheDeadline("temporal", "postgres");
    [Fact] public Task Temporal_abandons_an_attempt_at_its_deadline_and_retries_it_on_SqlServer() => BindingPlacesTheDeadline("temporal", "sqlserver");
    [Fact] public Task Flowable_abandons_an_attempt_at_its_deadline_and_retries_it_on_PostgreSql() => BindingPlacesTheDeadline("flowable", "postgres");
    [Fact] public Task Flowable_abandons_an_attempt_at_its_deadline_and_retries_it_on_SqlServer() => BindingPlacesTheDeadline("flowable", "sqlserver");

    /// <summary>
    /// R-09-299, R-09-310 (Issue I-401), as <see cref="OutlivesItsTimeout"/> is for DTFx and Elsa: on Temporal and Flowable the binding
    /// places an attempt's deadline (Temporal's Start-To-Close on a real server, Flowable's attempt timer boundary armed when the binding
    /// begins the attempt). A worker's call that outlives its three seconds (it takes ten) is abandoned at its deadline, and the retry
    /// reaches the worker while the first call is still out. The first call's late answer is ignored: the payment's facts hold the attempt
    /// that timed out and the one that answered, never the late answer's writes, and the payment takes the retry's answer.
    /// </summary>
    async Task BindingPlacesTheDeadline(string engine, string database)
    {
        await UseStore(database);
        using var worker = new Worker { TagAttempts = true };
        var (bank, dist, plans, workflow) = await Distribution(engine, "tb", "PT1S", worker, timeout: true);
        var p = $"T2-{bank}";
        var slow = TimeSpan.FromSeconds(10);
        worker.AttemptDelay = (payment, step, attempt) => payment == p && step == "a" && attempt == 1 ? slow : TimeSpan.Zero;
        Supervisor? sup = null;
        try
        {
            sup = await Current(engine, bank, plans, dist);
            var r = await sup.Call("POST", "/payments", new JsonObject { ["paymentId"] = p, ["context"] = Context(p), ["workflow"] = "p.wf.timeout.v1" });
            Assert.True(r.status == 202, r.body.ToJsonString());
            var e = await Ended(sup, p);
            Assert.True(J.S(e, "outcome") == "done", e.ToJsonString());
            // The retry reached the worker after the deadline and before the first call answered: the first was abandoned, not awaited.
            await Until(() => Task.FromResult(worker.Timed.Count(x => x.call == $"{p}:a") >= 2), TimeSpan.FromSeconds(30), "the first call's late answer");
            var calls = string.Join(", ", worker.Timed.Select(x => $"{x.call}#{x.attempt} at {x.at:O} answered {x.answered:O}"));
            var a1 = worker.Timed.FirstOrDefault(x => x.call == $"{p}:a" && x.attempt == 1);
            var a2 = worker.Timed.FirstOrDefault(x => x.call == $"{p}:a" && x.attempt == 2);
            Assert.True(a1.call is not null && a2.call is not null, calls);
            Assert.True(a2.at - a1.at >= TimeSpan.FromSeconds(2.5), $"the retry came {a2.at - a1.at} after the first attempt: before its deadline ({calls})");
            Assert.True(a2.at < a1.answered, $"the retry came at {a2.at:O}, after the first call answered at {a1.answered:O}: the attempt was not abandoned at its deadline");
            Assert.Equal(1, worker.Timed.Count(x => x.call == $"{p}:b"));
            // The payment took the retry's answer, never the late one.
            var ctx = J.P(J.A(J.P(e, "observation"), "instances").FirstOrDefault(), "ctx");
            if (ctx is not null) Assert.Equal("1.0.0#2", J.S(J.P(ctx, "facts"), "a"));
            // The payment's facts: step a timed out on its first attempt and answered on its second; nothing of the late answer, though
            // it came after the payment had ended.
            await Task.Delay(TimeSpan.FromSeconds(2));
            var subject = await SubjectOf(p) ?? throw new Xunit.Sdk.XunitException($"{p} was not accepted");
            await using (var db = StoreDbContext.Open(Db))
            {
                var steps = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subject && f.Kind == "step").OrderBy(f => f.Seq).ToListAsync();
                // By attempt: on Temporal the attempt that timed out is told a second after its Start-To-Close, when the retry may already have answered.
                var ofA = steps.Select(f => (f.Code, d: JsonNode.Parse(f.DataJson)!)).Where(x => J.S(x.d, "step") == "a").OrderBy(x => (int)(J.Num(x.d, "attempt") ?? 0)).ToList();
                var shown = string.Join("; ", ofA.Select(x => $"{x.Code} {x.d.ToJsonString()}"));
                Assert.DoesNotContain(ofA, x => x.d.ToJsonString().Contains("1.0.0#1"));
                Assert.True(ofA.Count >= 1 && ofA[^1].Code == "ok" && (int)(J.Num(ofA[^1].d, "attempt") ?? 0) == 2, shown);
                Assert.Equal("1.0.0#2", (string?)ofA[^1].d["writes"]?["facts.a"]);
                Assert.True(ofA.Count == 2 && ofA[0].Code == "error" && J.S(J.P(ofA[0].d, "error"), "name") == "Timeout" && (int)(J.Num(ofA[0].d, "attempt") ?? 0) == 1, shown);
            }
            await ChainsIntact(p);
        }
        catch (Exception x) when (x is not Xunit.Sdk.XunitException || !x.Message.Contains("--- the supervisor's log ---"))
        {
            throw new Xunit.Sdk.XunitException($"{x.Message}\n{x.StackTrace}\n--- the supervisor's log ---\n{sup?.Log}");
        }
        finally
        {
            if (sup is not null) await sup.DisposeAsync();
            System.Environment.SetEnvironmentVariable("TH_HOST_WORKERS", null);
            try { Directory.Delete(plans, true); } catch { }
        }
    }

    [Fact] public Task Dtfx_does_not_count_an_attempts_wait_for_a_slot_against_its_timeout_on_PostgreSql() => WaitsForASlot("dtfx", "postgres");
    [Fact] public Task Dtfx_does_not_count_an_attempts_wait_for_a_slot_against_its_timeout_on_SqlServer() => WaitsForASlot("dtfx", "sqlserver");
    [Fact] public Task Elsa_does_not_count_an_attempts_wait_for_a_slot_against_its_timeout_on_PostgreSql() => WaitsForASlot("elsa", "postgres");
    [Fact] public Task Elsa_does_not_count_an_attempts_wait_for_a_slot_against_its_timeout_on_SqlServer() => WaitsForASlot("elsa", "sqlserver");

    /// <summary>
    /// R-09-307 (Issue I-397; R-05-54: the time an attempt waits in a queue does not count against its timeout). With one slot
    /// (TH_HOST_SERVICE_CONCURRENCY=1), a payment's two branches schedule their attempts at once, each with three seconds; each call
    /// takes two. The second waits two seconds for the slot and then runs its two seconds: it answers within its own three, so it is
    /// taken, first time. Before, its deadline counted from when it was scheduled, so it was abandoned a second into its call and retried.
    /// </summary>
    async Task WaitsForASlot(string engine, string database)
    {
        await UseStore(database);
        using var worker = new Worker { TagAttempts = true };
        var (bank, dist, plans, _) = await Distribution(engine, "slot", "PT1S", worker, timeout: true);
        var p = $"Q1-{bank}";
        worker.AttemptDelay = (payment, step, _) => payment == p ? TimeSpan.FromSeconds(2) : TimeSpan.Zero;
        System.Environment.SetEnvironmentVariable("TH_HOST_SERVICE_CONCURRENCY", "1");
        Supervisor? sup = null;
        try
        {
            sup = await Current(engine, bank, plans, dist);
            var r = await sup.Call("POST", "/payments", new JsonObject { ["paymentId"] = p, ["context"] = Context(p), ["workflow"] = "p.wf.queued.v1" });
            Assert.True(r.status == 202, r.body.ToJsonString());
            var e = await Ended(sup, p);
            Assert.True(J.S(e, "outcome") == "done", e.ToJsonString());
            // One attempt of each step reached the worker, one after the other: the second waited for the slot.
            var calls = worker.Timed.Where(x => x.call.StartsWith($"{p}:")).OrderBy(x => x.at).ToList();
            Assert.True(calls.Count == 2, string.Join(", ", calls.Select(x => $"{x.call}#{x.attempt} at {x.at:O}")));
            Assert.All(calls, x => Assert.Equal(1, x.attempt));
            Assert.True(calls[1].at >= calls[0].answered - TimeSpan.FromMilliseconds(200), $"the second call came at {calls[1].at:O}, before the first answered at {calls[0].answered:O}: it did not wait for the slot");
            // Neither attempt timed out, and no answer was late.
            var subject = await SubjectOf(p) ?? throw new Xunit.Sdk.XunitException($"{p} was not accepted");
            await using (var db = StoreDbContext.Open(Db))
            {
                var steps = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subject && f.Kind == "step").OrderBy(f => f.Seq).ToListAsync();
                var of = steps.Select(f => (f.Code, d: JsonNode.Parse(f.DataJson)!)).Where(x => J.S(x.d, "step") is "a" or "c").ToList();
                Assert.True(of.All(x => x.Code == "ok") && of.Count == 2, string.Join("; ", of.Select(x => $"{x.Code} {x.d.ToJsonString()}")));
                Assert.All(of, x => Assert.Equal(1, (int)(J.Num(x.d, "attempt") ?? 0)));
            }
            Assert.DoesNotContain(await EngineChain(engine, dist), f => f.Code == "late-answer");
            await ChainsIntact(p);
        }
        catch (Exception x) when (x is not Xunit.Sdk.XunitException || !x.Message.Contains("--- the supervisor's log ---"))
        {
            throw new Xunit.Sdk.XunitException($"{x.Message}\n{x.StackTrace}\n--- the supervisor's log ---\n{sup?.Log}");
        }
        finally
        {
            if (sup is not null) await sup.DisposeAsync();
            System.Environment.SetEnvironmentVariable("TH_HOST_SERVICE_CONCURRENCY", null);
            System.Environment.SetEnvironmentVariable("TH_HOST_WORKERS", null);
            try { Directory.Delete(plans, true); } catch { }
        }
    }

    [Fact] public Task Elsa_serves_many_payments_of_a_distribution_on_one_service() => OneService("elsa");
    [Fact] public Task Flowable_serves_many_payments_of_a_distribution_from_one_deployment() => OneService("flowable");
}
