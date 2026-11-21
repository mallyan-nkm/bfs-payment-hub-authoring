using System.Text.Json;
using System.Text.Json.Nodes;
using Temporalio.Workflows;
using TransactHub.Engines;

namespace TransactHub.Engine.Temporal;

/// <summary>
/// The front door (spec Part 5, 5.3), as one long-running workflow, so that everything that happens at
/// a time happens on Temporal's timers: roots start at their times, signals are raised at theirs, and a
/// party's later messages arrive when the party said. A message that starts a handler runs it, outside
/// the durable engine (an activity, R-09-14); any other is delivered by correlation to the run it
/// belongs to, which keeps it if early and ignores a copy; one that finds no run is unmatched.
/// </summary>
[Workflow(Name)]
public sealed class FrontDoorWorkflow
{
    public const string Name = "transacthub.front-door";
    public const string Deliver = "deliver", Register = "register", Completed = "completed";
    public const string HandlerActivity = "transacthub.handler";

    sealed class Run
    {
        public string Id = "", Kind = "";
        public JsonNode? Header;
        public bool Done;
        public List<string> Consumed = new();
    }

    readonly long offset;
    readonly List<Run> runs = new();
    readonly List<JsonObject> handlerOutputs = new();
    readonly List<string> log = new();
    int pending, unmatched, duplicates, handlerSeq;

    [WorkflowInit]
    public FrontDoorWorkflow(string input) => offset = (long)J.Num((JsonObject)J.Parse(input)!, "offset")!.Value;

    long Now => new DateTimeOffset(Workflow.UtcNow, TimeSpan.Zero).ToUnixTimeMilliseconds() + offset;
    Task Until(long at) => Workflow.DelayAsync(TimeSpan.FromMilliseconds(Math.Max(0, at - Now)));

    [WorkflowRun]
    public async Task<string> RunAsync(string input)
    {
        var inp = (JsonObject)J.Parse(input)!;
        var expected = J.Strings(J.P(inp, "roots"));
        var handlerOnly = J.Truthy(J.P(inp, "handlerOnly"));
        var horizon = (long)J.Num(inp, "horizon")!.Value;
        foreach (var s in J.A(inp, "starts")) _ = StartLater((JsonObject)s!);
        foreach (var s in J.A(inp, "signals")) _ = SignalLater((JsonObject)s!);
        Accept(J.A(inp, "deliveries"));

        bool done() => pending == 0 && (handlerOnly ? handlerOutputs.Count > 0 : expected.All(id => runs.Any(r => r.Id == id && r.Done)));
        var finished = await Workflow.WaitConditionAsync(done, TimeSpan.FromMilliseconds(Math.Max(1, horizon - Now)));
        if (!finished) log.Add("the horizon was reached before every root ended");
        return J.Str(new JsonObject
        {
            ["unmatched"] = unmatched, ["duplicates"] = duplicates, ["handlerOutputs"] = new JsonArray(handlerOutputs.Select(o => J.Clone(o)).ToArray()),
            ["runs"] = new JsonArray(runs.Select(r => (JsonNode?)new JsonObject { ["id"] = r.Id, ["kind"] = r.Kind, ["done"] = r.Done }).ToArray()),
            ["log"] = J.Arr(log), ["end"] = Now,
        });
    }

    async Task StartLater(JsonObject s)
    {
        pending++;
        try
        {
            var at = (long)J.Num(s, "at")!.Value;
            await Until(at);
            await Workflow.StartChildWorkflowAsync((InterpreterWorkflow wf) => wf.RunAsync(J.Str(Envelope.Wrap((JsonObject)J.Clone(J.P(s, "input"))!, offset, Workflow.Info.WorkflowId, at))),
                new ChildWorkflowOptions { Id = J.S(s, "id"), ParentClosePolicy = Temporalio.Workflows.ParentClosePolicy.Abandon, TaskQueue = Workflow.Info.TaskQueue });
        }
        finally { pending--; }
    }

    async Task SignalLater(JsonObject s)
    {
        var at = (long)J.Num(s, "at")!.Value;
        await Until(at);
        var id = J.S(s, "id")!;
        if (runs.FirstOrDefault(r => r.Id == id) is { Done: false })
            await Workflow.GetExternalWorkflowHandle(id).SignalAsync("signal", new object?[] { J.Str(new JsonObject { ["signal"] = J.S(s, "signal") }), at });
    }

    void Accept(JsonArray deliveries)
    {
        foreach (var d in deliveries.ToList())
        {
            pending++;
            _ = DeliverLater((long)J.Num(d, "at")!.Value, (JsonObject)J.Clone(J.P(d, "message"))!);
        }
    }

    async Task DeliverLater(long at, JsonObject m)
    {
        try
        {
            await Until(at);
            await FrontDoor(m, at);
        }
        finally { pending--; }
    }

    [WorkflowSignal(Deliver)]
    public Task DeliverAsync(string data)
    {
        var d = (JsonObject)J.Parse(data)!;
        if (J.S(d, "ackTo") is not string ackTo) { Accept(J.A(d, "deliveries")); return Task.CompletedTask; }
        // A run hands over what its party sent: what is due now is routed at once, in order, then the
        // run is told, so it goes on only once its early messages are in its mailbox.
        var now = new List<(long at, JsonObject m)>();
        foreach (var x in J.A(d, "deliveries").ToList())
        {
            var at = (long)J.Num(x, "at")!.Value;
            var m = (JsonObject)J.Clone(J.P(x, "message"))!;
            if (at <= Now) now.Add((at, m));
            else { pending++; _ = DeliverLater(at, m); }
        }
        pending++;
        _ = RouteThenAck(now, ackTo, (int)J.Num(d, "ack")!.Value);
        return Task.CompletedTask;
    }

    async Task RouteThenAck(List<(long at, JsonObject m)> due, string ackTo, int ack)
    {
        try
        {
            foreach (var (at, m) in due.OrderBy(x => x.at)) await FrontDoor(m, at);
            await Workflow.GetExternalWorkflowHandle(ackTo).SignalAsync("ack", new object?[] { ack });
        }
        finally { pending--; }
    }

    [WorkflowSignal(Register)]
    public Task RegisterAsync(string data)
    {
        var d = (JsonObject)J.Parse(data)!;
        var id = J.S(d, "id")!;
        // A run that continues as new registers again under the same id: it is the same run.
        var r = runs.FirstOrDefault(x => x.Id == id);
        if (r is null) runs.Add(r = new Run { Id = id });
        r.Kind = J.S(d, "kind") ?? "workflow";
        r.Header = J.Clone(J.P(d, "header"));
        r.Done = false;
        return Task.CompletedTask;
    }

    [WorkflowSignal(Completed)]
    public Task CompletedAsync(string data)
    {
        var d = (JsonObject)J.Parse(data)!;
        if (runs.FirstOrDefault(x => x.Id == J.S(d, "id")) is { } r)
        {
            r.Done = true;
            r.Consumed = J.Strings(J.P(d, "consumed"));
        }
        return Task.CompletedTask;
    }

    [WorkflowQuery("state")]
    public string State() => J.Str(new JsonObject { ["pending"] = pending, ["runs"] = runs.Count, ["unmatched"] = unmatched, ["duplicates"] = duplicates });

    static bool Correlates(JsonNode? header, JsonObject m)
    {
        if (J.S(m, "conversation") is string conv && J.S(header, "conversation") == conv) return true;
        var c = J.P(m, "correlation");
        foreach (var k in new[] { "paymentId", "endToEndId", "fileId" })
            if (J.S(c, k) is string v && J.S(header, k) == v) return true;
        return false;
    }

    async Task FrontDoor(JsonObject m, long at)
    {
        var dep = Deployment.Current;
        var type = J.S(m, "type")!;
        var entry = dep.CatalogueEntry(type);
        var disp = J.P(entry, "disposition");
        if (J.Has(disp, "start") || J.Has(disp, "handler"))
        {
            if (J.Truthy(J.P(m, "copy"))) { duplicates++; return; }
            if (J.S(disp, "handler") is string handler) await StartHandler(m, entry!, handler, at);
            else { log.Add($"{type} starts a workflow through intake, which this harness does not run"); unmatched++; }
            return;
        }
        var candidates = runs.Where(r => r.Kind != "batch" && Correlates(r.Header, m)).ToList();
        var target = candidates.LastOrDefault(c => !c.Done) ?? candidates.LastOrDefault();
        if (target is null) { unmatched++; return; }
        if (target.Done)
        {
            var key = J.S(m, "inReplyTo") is string irt ? $"{type}:{irt}:{J.S(m, "status") ?? ""}" : J.S(m, "id")!;
            if (candidates.Where(c => c.Done).Any(c => c.Consumed.Contains(key))) duplicates++; else unmatched++;
            return;
        }
        await Workflow.GetExternalWorkflowHandle(target.Id).SignalAsync("message", new object?[] { J.Str(m), at });
    }

    async Task StartHandler(JsonObject m, JsonObject entry, string handler, long at)
    {
        var dep = Deployment.Current;
        var docv = dep.Resolve(handler, null, at - dep.Start);
        if (docv is null) { unmatched++; return; }
        var type = J.S(m, "type")!;
        var scope = string.Join('.', type.Split('.').Take(2));
        var id = $"handler-{++handlerSeq}";
        var ctx = new JsonObject
        {
            ["header"] = new JsonObject { ["conversation"] = J.S(m, "conversation"), ["scheme"] = scope, ["direction"] = J.S(entry, "direction") == "in" ? "inward" : "outward", ["receivedAt"] = Duration.Iso(at) },
            ["messages"] = new JsonObject { ["inbound"] = new JsonObject { ["id"] = J.S(m, "id"), ["type"] = type } },
        };
        var req = new JsonObject { ["id"] = id, ["doc"] = $"{J.S(docv, "name")}@{J.S(docv, "release")}", ["ctx"] = ctx, ["now"] = at };
        var raw = await Workflow.ExecuteActivityAsync<string>(HandlerActivity, new object?[] { J.Str(req) },
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(10), RetryPolicy = new() { MaximumAttempts = 1 } });
        var res = (JsonObject)J.Parse(raw)!;
        Accept(J.A(res, "deliveries"));
        handlerOutputs.Add((JsonObject)J.Clone(J.P(res, "output"))!);
    }

    internal static string Q(JsonNode? n) => n is null ? "null" : JsonSerializer.Serialize(n);
}
