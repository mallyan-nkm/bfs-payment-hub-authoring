using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TransactHub.Engine.Flowable;
using TransactHub.Engines;

namespace TransactHub.Engine.Flowable.Tests;

/// <summary>The Flowable REST server, pinned to the kit harness's Flowable (7.1.0), with its async executor off. One for the whole run.</summary>
public sealed class FlowableServer : IAsyncLifetime
{
    public const string User = "th-binding", Password = "th-test-only";
    readonly IContainer container = new ContainerBuilder("flowable/flowable-rest:7.1.0@sha256:b7ae287502cd5c243b577a0ca2894172e1c18cd75aea4fd67dc7d58f1b40db44")
        .WithEnvironment("FLOWABLE_ASYNC_EXECUTOR_ACTIVATE", "false")
        .WithEnvironment("FLOWABLE_ASYNC_HISTORY_EXECUTOR_ACTIVATE", "false")
        .WithEnvironment("FLOWABLE_REST_APP_CREATE_DEMO_DEFINITIONS", "false")
        .WithEnvironment("FLOWABLE_REST_APP_ADMIN_USER_ID", User)
        .WithEnvironment("FLOWABLE_REST_APP_ADMIN_PASSWORD", Password)
        .WithPortBinding(8080, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/flowable-rest/service/management/engine").WithBasicAuthentication(User, Password)))
        .Build();

    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
    public FlowableRest Client() => new($"http://{container.Hostname}:{container.GetMappedPublicPort(8080)}/flowable-rest", User, Password);
}

[CollectionDefinition(Name)]
public sealed class FlowableCollection : ICollectionFixture<FlowableServer> { public const string Name = "flowable"; }

static class Docs
{
    public const string Head = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:flowable="http://flowable.org/bpmn" xmlns:th="urn:transacthub:flowable:1" targetNamespace="urn:test">
          <error id="err_TransientError" errorCode="TransientError"/>
        """;

    /// <summary>
    /// One call with a retry loop, written as the studio's generator writes it for a REST binding: the
    /// binding's tasks (processStart, call, end) on th.binding, the attempt an external worker task with
    /// a retryable error boundary and a one-hour backoff timer.
    /// </summary>
    public static string OneCall(string key) => Head + $$"""
          <process id="{{key}}" isExecutable="true">
            <extensionElements><th:workflow name="t.wf.one.v1" release="1.0.0" kind="workflow"/></extensionElements>
            <startEvent id="start"/>
            <serviceTask id="th_started" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="processStart"/></extensionElements></serviceTask>
            <exclusiveGateway id="again"/>
            <serviceTask id="check_task" flowable:type="external-worker" flowable:topic="th-p:p.check.thing.v1@1.0.0" flowable:exclusive="false">
              <extensionElements>
                <flowable:executionListener event="start" expression="${execution.setVariable('th_attempt', (execution.getVariable('th_attempt') == null ? 0 : execution.getVariable('th_attempt')) + 1)}"/>
                <th:task step="check" activity="p.check.thing.v1" release="1.0.0" record="business"/>
              </extensionElements>
            </serviceTask>
            <boundaryEvent id="check_retry" attachedToRef="check_task"><errorEventDefinition errorRef="err_TransientError"/></boundaryEvent>
            <intermediateCatchEvent id="check_backoff"><timerEventDefinition><timeDuration>PT1H</timeDuration></timerEventDefinition></intermediateCatchEvent>
            <serviceTask id="th_rec_1" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="call" step="check" var="outcome_check" record="business"/></extensionElements></serviceTask>
            <intermediateThrowEvent id="th_endpoint_done"><extensionElements><flowable:executionListener event="start" expression="${execution.setVariable('th_outcome', 'done')}"/></extensionElements></intermediateThrowEvent>
            <serviceTask id="th_end" flowable:type="external-worker" flowable:topic="th.binding" flowable:exclusive="false"><extensionElements><th:point kind="end"/></extensionElements></serviceTask>
            <endEvent id="end"/>
            <sequenceFlow id="f1" sourceRef="start" targetRef="th_started"/>
            <sequenceFlow id="f2" sourceRef="th_started" targetRef="again"/>
            <sequenceFlow id="f3" sourceRef="again" targetRef="check_task"/>
            <sequenceFlow id="f4" sourceRef="check_retry" targetRef="check_backoff"/>
            <sequenceFlow id="f5" sourceRef="check_backoff" targetRef="again"/>
            <sequenceFlow id="f6" sourceRef="check_task" targetRef="th_rec_1"/>
            <sequenceFlow id="f7" sourceRef="th_rec_1" targetRef="th_endpoint_done"/>
            <sequenceFlow id="f8" sourceRef="th_endpoint_done" targetRef="th_end"/>
            <sequenceFlow id="f9" sourceRef="th_end" targetRef="end"/>
          </process>
        </definitions>
        """;

    /// <summary>Two external tasks in a row, the first with an error boundary: for the worker client on its own.</summary>
    public static string TwoTasks(string key) => Head + $$"""
          <process id="{{key}}" isExecutable="true">
            <startEvent id="start"/>
            <serviceTask id="first" flowable:type="external-worker" flowable:topic="t-first" flowable:exclusive="false"/>
            <boundaryEvent id="first_error" attachedToRef="first"><errorEventDefinition errorRef="err_TransientError"/></boundaryEvent>
            <serviceTask id="caught" flowable:type="external-worker" flowable:topic="t-caught" flowable:exclusive="false"/>
            <endEvent id="end"/>
            <sequenceFlow id="f1" sourceRef="start" targetRef="first"/>
            <sequenceFlow id="f2" sourceRef="first" targetRef="end"/>
            <sequenceFlow id="f3" sourceRef="first_error" targetRef="caught"/>
            <sequenceFlow id="f4" sourceRef="caught" targetRef="end"/>
          </process>
        </definitions>
        """;
}

public class BpmnModelTests
{
    [Fact]
    public void Reads_the_key_the_workflow_the_annotations_and_the_topics()
    {
        var p = BpmnProcess.Parse(Docs.OneCall("t_wf_one_v1_1_0_0"));
        Assert.Equal("t_wf_one_v1_1_0_0", p.Key);
        Assert.Equal("t.wf.one.v1", p.Workflow["name"]);
        Assert.Equal("check", p.Ann("check_task", "task")!["step"]);
        Assert.Equal("processStart", p.Ann("th_started", "point")!["kind"]);
        Assert.Null(p.Ann("again", "point"));
        Assert.Equal(["th-p:p.check.thing.v1@1.0.0", "th.binding"], p.Topics.Order(StringComparer.Ordinal).ToArray());
        Assert.Null(p.UndoOf("check_task"));
    }

    [Fact]
    public void A_task_inside_a_compensation_handler_knows_it()
    {
        var xml = Docs.Head + """
              <process id="u" isExecutable="true">
                <subProcess id="s_undo" isForCompensation="true"><extensionElements><th:point kind="undo" step="reserve"/></extensionElements>
                  <serviceTask id="s_undo_task" flowable:type="external-worker" flowable:topic="q:x@1"/>
                </subProcess>
              </process>
            </definitions>
            """;
        var p = BpmnProcess.Parse(xml);
        Assert.Equal("reserve", p.UndoOf("s_undo_task")!["step"]);
        Assert.Equal(["s_undo_task", "s_undo"], p.Ancestry("s_undo_task").ToArray());
    }

    [Fact]
    public void A_rest_variable_takes_its_type_from_the_value()
    {
        Assert.Equal("string", J.S(FlowableRest.Var("a", JsonValue.Create("x")), "type"));
        Assert.Equal("integer", J.S(FlowableRest.Var("a", JsonValue.Create(3)), "type"));
        Assert.Equal("double", J.S(FlowableRest.Var("a", JsonValue.Create(2.5)), "type"));
        Assert.Equal("boolean", J.S(FlowableRest.Var("a", JsonValue.Create(true)), "type"));
        Assert.Equal("json", J.S(FlowableRest.Var("a", new JsonObject { ["x"] = 1 }), "type"));
    }

    [Fact]
    public void A_process_key_is_the_generators()
    {
        Assert.Equal("p_wf_corporate_v1_1_0_0", FlowableBinding.Safe("p.wf.corporate.v1@1.0.0"));
    }
}

/// <summary>The party of a deployment, played here: the check fails once with a retryable error, then passes.</summary>
sealed class FakeParty : IParty
{
    public readonly List<JsonObject> Attempts = new();
    public Task<JsonObject> Call(JsonObject req)
    {
        switch (J.S(req, "op"))
        {
            case "attempt":
                Attempts.Add((JsonObject)J.Clone(req)!);
                return Task.FromResult(Attempts.Count == 1
                    ? new JsonObject { ["kind"] = "error", ["name"] = "TransientError", ["retryable"] = true, ["message"] = "try again" }
                    : new JsonObject { ["kind"] = "result", ["outcome"] = "ok", ["writes"] = new JsonObject { ["payment.checked"] = true, ["facts.check"] = "done" } });
            case "start": return Task.FromResult(new JsonObject { ["deliveries"] = new JsonArray() });
            case "sent": return Task.FromResult(new JsonObject { ["messages"] = new JsonArray() });
            default: return Task.FromResult(new JsonObject { ["ok"] = true });
        }
    }
    public void Dispose() { }
}

[Collection(FlowableCollection.Name)]
public class FlowableRestTests(FlowableServer server)
{
    [Fact]
    public async Task The_worker_client_acquires_fails_with_retries_throws_a_bpmn_error_and_completes()
    {
        using var rest = server.Client();
        var tenant = $"t-{Guid.NewGuid():N}"[..12];
        var key = $"two_{Guid.NewGuid():N}"[..12];
        var dep = await rest.Deploy("two", new Dictionary<string, string> { ["two.bpmn20.xml"] = Docs.TwoTasks(key) }, tenant);
        try
        {
            var pi = J.S(await rest.StartProcess(key, tenant, [new("amountRef", JsonValue.Create("ref-1"))]), "id")!;
            var first = Assert.Single(await rest.Acquire("t-first", 10, TimeSpan.FromMinutes(1), "w", tenant));
            Assert.Equal(pi, first.ProcessInstanceId);
            Assert.Equal("ref-1", J.AsKey(first.Variables["amountRef"]));
            Assert.Empty(await rest.Acquire("t-first", 10, TimeSpan.FromMinutes(1), "w", tenant)); // locked

            // A failure with retries left gives up the lock, but with the async executor off nothing resets
            // the expired lock, so the job is not handed out again (docs/ISSUES.md I-54).
            Assert.True(await rest.Fail(first.Id, "w", "worker down", first.Retries - 1, TimeSpan.Zero));
            var failed = Assert.Single(await rest.ExternalJobs(tenant));
            Assert.Null(J.S(failed, "lockOwner"));
            Assert.Equal(first.Retries - 1, (int)J.Num(failed, "retries")!);
            Assert.Equal("worker down", J.S(failed, "exceptionMessage"));
            Assert.Empty(await rest.Acquire("t-first", 10, TimeSpan.FromMinutes(1), "w", tenant));

            // A second instance: a failure with no retries left is a dead letter.
            var pi2 = J.S(await rest.StartProcess(key, tenant), "id")!;
            var spent = Assert.Single(await rest.Acquire("t-first", 10, TimeSpan.FromMinutes(1), "w", tenant));
            Assert.True(await rest.Fail(spent.Id, "w", "worker down for good", 0, TimeSpan.Zero));
            var dead = Assert.Single(await rest.DeadLetterJobs(tenant));
            Assert.Equal(pi2, J.S(dead, "processInstanceId"));

            // A third: a BPMN error is caught on the task's boundary once the engine runs the completion job.
            pi = J.S(await rest.StartProcess(key, tenant), "id")!;
            var again = Assert.Single(await rest.Acquire("t-first", 10, TimeSpan.FromMinutes(1), "w", tenant));
            Assert.True(await rest.BpmnError(again.Id, "w", "TransientError"));
            foreach (var j in await rest.Jobs(tenant)) Assert.Null(await rest.ExecuteJob(J.S(j, "id")!));
            var caught = Assert.Single(await rest.Acquire("t-caught", 10, TimeSpan.FromMinutes(1), "w", tenant));
            Assert.True(await rest.Complete(caught.Id, "w", [new("outcome_caught", JsonValue.Create("ok"))]));
            foreach (var j in await rest.Jobs(tenant)) Assert.Null(await rest.ExecuteJob(J.S(j, "id")!));
            Assert.Empty(await rest.Executions(pi));
            // A job that is gone is not an error: the binding treats it as a result that came too late.
            Assert.False(await rest.Complete(caught.Id, "w"));
        }
        finally { await rest.DeleteDeployment(dep); }
    }

    [Fact]
    public async Task The_binding_runs_a_call_with_a_retry_on_its_own_clock_and_keeps_the_payload_out_of_the_engine()
    {
        using var rest = server.Client();
        var key = FlowableBinding.Safe("t.wf.one.v1@1.0.0");
        var input = new JsonObject
        {
            ["built"] = new JsonObject
            {
                ["id"] = "T-1", ["spec"] = new JsonObject(), ["workflow"] = "t.wf.one.v1",
                ["config"] = new JsonObject { ["evaluationDate"] = "2026-11-17", ["tenant"] = "test-bank" },
                ["context"] = new JsonObject
                {
                    ["header"] = new JsonObject { ["conversation"] = "conv-1", ["scheme"] = "p", ["direction"] = "outward" },
                    ["payment"] = new JsonObject { ["endToEndId"] = "E2E-T-1", ["instructedAmount"] = new JsonObject { ["value"] = 10, ["currency"] = "AED" } },
                },
            },
            ["documents"] = new JsonObject { ["t.wf.one.v1@1.0.0"] = new JsonObject { ["kind"] = "workflow", ["name"] = "t.wf.one.v1", ["release"] = "1.0.0" } },
            ["bpmn"] = new JsonObject { ["t.wf.one.v1@1.0.0.bpmn20.xml"] = Docs.OneCall(key) },
        };
        var party = new FakeParty();
        var obs = await new FlowableBinding(input, rest, party).Run();
        var root = (JsonObject)J.A(obs, "instances")[0]!;
        Assert.Equal(["check:ok", "end:done"], J.Strings(J.P(root, "trace")));
        Assert.Equal(2, (int)J.Num(J.P(root, "attempts"), "check")!);
        Assert.Equal([1, 2], party.Attempts.Select(a => (int)J.Num(a, "attempt")!).ToArray());
        // The backoff timer fired on the binding's clock: one hour later, in no wall time.
        Assert.Equal(DateTimeOffset.Parse("2026-11-17T06:00:00Z").ToUnixTimeMilliseconds(), (long)J.Num(party.Attempts[0], "now")!);
        Assert.Equal(DateTimeOffset.Parse("2026-11-17T07:00:00Z").ToUnixTimeMilliseconds(), (long)J.Num(party.Attempts[1], "now")!);
        Assert.Equal(1, (int)J.Num(J.P(obs, "engine"), "timersFired")!);
        // The payload is in the claim check; the engine held flow variables and references only.
        Assert.True(J.Truthy(J.Get(J.P(root, "ctx"), "payment.checked")));
        Assert.Equal("done", J.S(J.Get(J.P(root, "ctx"), "facts"), "check"));
        var keys = J.Strings(J.P(root, "engineStateKeys"));
        Assert.DoesNotContain("payment", keys);
        Assert.Contains("th_attempt", keys);
        Assert.Empty(J.A(obs, "notes"));
    }
}
