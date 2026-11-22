using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TransactHub.Engine.Flowable;
using TransactHub.Engines;

namespace TransactHub.Engine.Flowable.Tests;

/// <summary>
/// The Flowable REST server as production runs it: its async executor on, in real time. The executor's
/// polling is shortened (a timer or a reset lock is looked for every second) so that the tests take seconds.
/// </summary>
public sealed class FlowableLiveServer : IAsyncLifetime
{
    readonly IContainer container = new ContainerBuilder("flowable/flowable-rest:7.1.0@sha256:b7ae287502cd5c243b577a0ca2894172e1c18cd75aea4fd67dc7d58f1b40db44")
        .WithEnvironment("FLOWABLE_ASYNC_EXECUTOR_ACTIVATE", "true")
        .WithEnvironment("FLOWABLE_PROCESS_ASYNC_EXECUTOR_DEFAULT_TIMER_JOB_ACQUIRE_WAIT_TIME", "PT1S")
        .WithEnvironment("FLOWABLE_PROCESS_ASYNC_EXECUTOR_DEFAULT_ASYNC_JOB_ACQUIRE_WAIT_TIME", "PT1S")
        .WithEnvironment("FLOWABLE_PROCESS_ASYNC_EXECUTOR_RESET_EXPIRED_JOBS_INTERVAL", "PT1S")
        .WithEnvironment("FLOWABLE_REST_APP_CREATE_DEMO_DEFINITIONS", "false")
        .WithEnvironment("FLOWABLE_REST_APP_ADMIN_USER_ID", FlowableServer.User)
        .WithEnvironment("FLOWABLE_REST_APP_ADMIN_PASSWORD", FlowableServer.Password)
        .WithPortBinding(8080, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/flowable-rest/service/management/engine").WithBasicAuthentication(FlowableServer.User, FlowableServer.Password)))
        .Build();

    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
    public FlowableRest Client() => new($"http://{container.Hostname}:{container.GetMappedPublicPort(8080)}/flowable-rest", FlowableServer.User, FlowableServer.Password);
}

/// <summary>The party of a deployment whose worker does not answer the first <c>down</c> calls (or never, with -1), then passes.</summary>
sealed class DownParty(int down) : IParty
{
    public readonly List<JsonObject> Attempts = new();
    int calls;
    public Task<JsonObject> Call(JsonObject req)
    {
        switch (J.S(req, "op"))
        {
            case "attempt":
                calls++;
                if (down < 0 || calls <= down) throw new HttpRequestException("connection refused (the worker is down)");
                Attempts.Add((JsonObject)J.Clone(req)!);
                return Task.FromResult(new JsonObject { ["kind"] = "result", ["outcome"] = "ok", ["writes"] = new JsonObject { ["facts.check"] = "done" } });
            case "start": return Task.FromResult(new JsonObject { ["deliveries"] = new JsonArray() });
            case "sent": return Task.FromResult(new JsonObject { ["messages"] = new JsonArray() });
            default: return Task.FromResult(new JsonObject { ["ok"] = true });
        }
    }
    public void Dispose() { }
}

static class Live
{
    /// <summary>One call, with a backoff of two seconds, as the generator writes it for a REST binding.</summary>
    public static string OneCall(string key) => Docs.OneCall(key).Replace("<timeDuration>PT1H</timeDuration>", "<timeDuration>PT2S</timeDuration>");

    /// <summary>A wait whose duration is a parameter, or on the business calendar: the generator writes the default and marks it th:timer.</summary>
    public static string Wait(string key, string mark, string written) => Docs.Head + $$"""
          <process id="{{key}}" isExecutable="true">
            <extensionElements><th:workflow name="t.wf.wait.v1" release="1.0.0" kind="workflow"/></extensionElements>
            <startEvent id="start"/>
            <serviceTask id="th_started" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="processStart"/></extensionElements></serviceTask>
            <intermediateCatchEvent id="hold_wait" name="wait"><extensionElements>{{mark}}</extensionElements><timerEventDefinition><timeDuration>{{written}}</timeDuration></timerEventDefinition></intermediateCatchEvent>
            <intermediateThrowEvent id="th_endpoint_done"><extensionElements><flowable:executionListener event="start" expression="${execution.setVariable('th_outcome', 'done')}"/></extensionElements></intermediateThrowEvent>
            <serviceTask id="th_end" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="end"/></extensionElements></serviceTask>
            <endEvent id="end"/>
            <sequenceFlow id="f1" sourceRef="start" targetRef="th_started"/>
            <sequenceFlow id="f2" sourceRef="th_started" targetRef="hold_wait"/>
            <sequenceFlow id="f3" sourceRef="hold_wait" targetRef="th_endpoint_done"/>
            <sequenceFlow id="f4" sourceRef="th_endpoint_done" targetRef="th_end"/>
            <sequenceFlow id="f5" sourceRef="th_end" targetRef="end"/>
          </process>
        </definitions>
        """;

    public static JsonObject Plan(string workflow, string bpmn, string date = "2026-11-17", JsonArray? values = null) => new()
    {
        ["built"] = new JsonObject
        {
            ["id"] = "T-LIVE", ["spec"] = new JsonObject(), ["workflow"] = workflow,
            ["config"] = new JsonObject { ["evaluationDate"] = date, ["tenant"] = "test-bank", ["values"] = values ?? new JsonArray() },
            ["context"] = new JsonObject
            {
                ["header"] = new JsonObject { ["conversation"] = "conv-1", ["scheme"] = "p", ["direction"] = "outward" },
                ["payment"] = new JsonObject { ["endToEndId"] = "E2E-T-LIVE", ["instructedAmount"] = new JsonObject { ["value"] = 10, ["currency"] = "AED" } },
            },
        },
        ["documents"] = new JsonObject { [$"{workflow}@1.0.0"] = new JsonObject { ["kind"] = "workflow", ["name"] = workflow, ["release"] = "1.0.0" } },
        ["bpmn"] = new JsonObject { [$"{workflow}@1.0.0.bpmn20.xml"] = bpmn },
    };

    public static readonly FlowableOptions Production = new() { Mode = FlowableMode.Production, RetryWait = TimeSpan.FromSeconds(1), Poll = TimeSpan.FromMilliseconds(100), MaxWait = TimeSpan.FromMinutes(2) };
}

public class FlowableOptionsTests
{
    [Fact]
    public void The_mode_and_its_timings_come_from_the_environment()
    {
        var env = new Dictionary<string, string?> { ["TH_FLOWABLE_MODE"] = "production", ["TH_FLOWABLE_LOCK"] = "PT5S", ["TH_FLOWABLE_RETRY_WAIT"] = "PT2S", ["TH_FLOWABLE_SIM_SCALE"] = "0.01", ["TH_FLOWABLE_FAULT"] = "crash:post" };
        var o = FlowableOptions.From(k => env.GetValueOrDefault(k));
        Assert.True(o.Production);
        Assert.Equal(TimeSpan.FromSeconds(5), o.LockFor);
        Assert.Equal(TimeSpan.FromSeconds(2), o.RetryWait);
        Assert.Equal(0.01, o.SimScale);
        Assert.Equal("post", o.CrashAt);
        Assert.Null(o.DownAt);
        Assert.False(FlowableOptions.From(_ => null).Production);
        Assert.Throws<ArgumentException>(() => FlowableOptions.From(k => k == "TH_FLOWABLE_MODE" ? "fast" : null));
        Assert.Throws<ArgumentException>(() => FlowableOptions.From(k => k == "TH_FLOWABLE_FAULT" ? "slow:post" : null));
    }
}

/// <summary>Production mode (Issues I-53 to I-55): the server's async executor on, real time, the binding a pure external worker.</summary>
[Collection(FlowableCollection.Name)]
public class FlowableProductionTests(FlowableLiveServer server)
{
    static JsonObject Root(JsonObject obs) => (JsonObject)J.A(obs, "instances")[0]!;
    static JsonNode? Engine(JsonObject obs, string k) => J.P(J.P(obs, "engine"), k);

    [Fact]
    public async Task Flowable_runs_the_jobs_and_fires_the_backoff_timer_in_real_time()
    {
        using var rest = server.Client();
        var key = FlowableBinding.Safe("t.wf.one.v1@1.0.0");
        var party = new FakeParty();
        var obs = await new FlowableBinding(Live.Plan("t.wf.one.v1", Live.OneCall(key)), rest, party, Live.Production).Run();
        var root = Root(obs);
        Assert.Equal(["check:ok", "end:done"], J.Strings(J.P(root, "trace")));
        Assert.Equal(2, (int)J.Num(J.P(root, "attempts"), "check")!);
        Assert.Equal("production", J.S(J.P(obs, "engine"), "mode"));
        // The server ran its own jobs and fired the timer: the binding ran and moved none.
        Assert.Equal(0, (int)J.Num(J.P(obs, "engine"), "asyncJobs")!);
        Assert.Equal(0, (int)J.Num(J.P(obs, "engine"), "timersFired")!);
        // The second attempt came after the two-second backoff, in real time, from the plan's start.
        var gap = (long)J.Num(party.Attempts[1], "now")! - (long)J.Num(party.Attempts[0], "now")!;
        Assert.InRange(gap, 2000, 30_000);
        Assert.True((long)J.Num(party.Attempts[0], "now")! >= DateTimeOffset.Parse("2026-11-17T06:00:00Z").ToUnixTimeMilliseconds());
        Assert.True(J.Truthy(J.Get(J.P(root, "ctx"), "payment.checked")));
        Assert.Empty(J.A(obs, "notes"));
    }

    [Fact]
    public async Task A_worker_that_does_not_answer_is_retried_by_Flowable_after_its_retry_timeout()
    {
        using var rest = server.Client();
        var key = FlowableBinding.Safe("t.wf.one.v1@1.0.0");
        var party = new DownParty(2); // the job's three retries cover two failures
        var obs = await new FlowableBinding(Live.Plan("t.wf.one.v1", Live.OneCall(key)), rest, party, Live.Production).Run();
        var root = Root(obs);
        Assert.Equal(["check:ok", "end:done"], J.Strings(J.P(root, "trace")));
        // A technical failure is not an attempt; the job came back twice, the same attempt each time.
        Assert.Equal(1, (int)J.Num(J.P(root, "attempts"), "check")!);
        Assert.Equal(2, (int)J.Num(J.P(obs, "engine"), "retriedOnFailure")!);
        Assert.Equal(2, (int)J.Num(J.P(obs, "engine"), "handedOutAgain")!);
        Assert.Empty(J.A(J.P(obs, "engine"), "deadLetters"));
        Assert.Contains(J.Strings(Engine(obs, "log")), l => l.Contains("failed with 2 retries left, Flowable hands it out again after PT1S"));
    }

    [Fact]
    public async Task A_job_whose_retries_are_spent_is_a_dead_letter_and_the_binding_surfaces_it()
    {
        using var rest = server.Client();
        var key = FlowableBinding.Safe("t.wf.one.v1@1.0.0");
        var obs = await new FlowableBinding(Live.Plan("t.wf.one.v1", Live.OneCall(key)), rest, new DownParty(-1), Live.Production).Run();
        var root = Root(obs);
        var dead = (JsonObject)Assert.Single(J.A(J.P(obs, "engine"), "deadLetters"))!;
        Assert.Equal("check", J.S(dead, "step"));
        Assert.Equal("check_task", J.S(dead, "element"));
        Assert.Equal("root", J.S(dead, "payment"));
        Assert.Contains("worker is down", J.S(dead, "message"));
        Assert.StartsWith("dead letter at check:", J.S(root, "failed"));
        Assert.DoesNotContain("end:done", J.Strings(J.P(root, "trace")));
        Assert.Equal(0, (int)J.Num(J.P(root, "attempts"), "check")!);
        Assert.Contains(J.Strings(J.P(obs, "notes")), n => n.StartsWith("dead letter: the job of check (check_task)"));
    }

    [Fact]
    public async Task A_job_held_by_a_worker_that_crashed_comes_back_when_its_lock_expires()
    {
        using var rest = server.Client();
        var key = FlowableBinding.Safe("t.wf.one.v1@1.0.0");
        var party = new DownParty(0);
        var opts = Live.Production with { LockFor = TimeSpan.FromSeconds(3), CrashAt = "check" };
        var obs = await new FlowableBinding(Live.Plan("t.wf.one.v1", Live.OneCall(key)), rest, party, opts).Run();
        var root = Root(obs);
        Assert.Equal(["check:ok", "end:done"], J.Strings(J.P(root, "trace")));
        Assert.Equal(1, (int)J.Num(J.P(obs, "engine"), "crashes")!);
        Assert.Equal(1, (int)J.Num(J.P(root, "attempts"), "check")!);
        Assert.Single(party.Attempts);
        // Flowable handed the job out again only once the crashed worker's lock had expired.
        Assert.True((long)J.Num(party.Attempts[0], "now")! - DateTimeOffset.Parse("2026-11-17T06:00:00Z").ToUnixTimeMilliseconds() >= 3000);
        Assert.Contains(J.Strings(Engine(obs, "log")), l => l.Contains("the worker stopped holding job"));
    }

    [Fact]
    public async Task A_wait_from_a_parameter_is_placed_by_the_binding_and_fired_by_Flowable()
    {
        using var rest = server.Client();
        var key = FlowableBinding.Safe("t.wf.wait.v1@1.0.0");
        var values = new JsonArray(new JsonObject { ["parameter"] = "t.wf.wait.v1#hold", ["value"] = "PT3S", ["validFrom"] = "2026-01-01" });
        var bpmn = Live.Wait(key, """<th:timer parameter="t.wf.wait.v1#hold" default="P1D"/>""", "P1D");
        var obs = await new FlowableBinding(Live.Plan("t.wf.wait.v1", bpmn, values: values), rest, new FakeParty(), Live.Production).Run();
        Assert.Equal(["end:done"], J.Strings(J.P(Root(obs), "trace")));
        Assert.Equal(1, (int)J.Num(J.P(obs, "engine"), "timersPlaced")!);
        // The tenant's three seconds, not the day it was written with.
        Assert.InRange((long)J.Num(Root(obs), "endedAt")!, 3000, 60_000);
    }

    [Fact]
    public async Task A_wait_on_the_business_calendar_is_placed_on_the_next_business_day()
    {
        using var rest = server.Client();
        var key = FlowableBinding.Safe("t.wf.wait.v1@1.0.0");
        // Friday 20 November 2026, 06:00: one business day is Monday 23 November, 06:00 (the calendar's weekend is Saturday and Sunday).
        var bpmn = Live.Wait(key, """<th:timer calendar="business" duration="P1D"/>""", "P1D");
        var opts = Live.Production with { MaxWait = TimeSpan.FromSeconds(6) };
        var obs = await new FlowableBinding(Live.Plan("t.wf.wait.v1", bpmn, date: "2026-11-20"), rest, new FakeParty(), opts).Run();
        Assert.Equal(1, (int)J.Num(J.P(obs, "engine"), "timersPlaced")!);
        Assert.Contains(J.Strings(Engine(obs, "log")), l => l.StartsWith("timer hold_wait placed at 2026-11-23T06:00:0") && l.Contains("written 2026-11-21T06:00:0"));
        Assert.Contains(J.Strings(J.P(obs, "notes")), n => n.StartsWith("stopped after PT6S of real time with 1 payment(s) still running"));
    }
}
