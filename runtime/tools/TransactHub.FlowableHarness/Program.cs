using System.Text.Json;
using System.Text.Json.Nodes;
using TransactHub.Engine.Flowable;
using TransactHub.Engines;
using TransactHub.Telemetry;

// The Flowable harness: `dotnet TransactHub.FlowableHarness.dll <plan.json>`. It takes the plan the
// studio's deploy runner writes for every engine, with the package's BPMN beside it, runs it on the
// Flowable REST server at TH_FLOWABLE_URL (TH_FLOWABLE_USER, TH_FLOWABLE_PASSWORD) with this process
// as the external worker, and prints OBSERVATION <json>, as the DTFx, Elsa and Temporal harnesses do.
// With TH_STORE_PROVIDER and TH_STORE_CONNECTION the payloads are kept in the store. TH_FLOWABLE_MODE=production
// runs it with the server's async executor on and real time (FlowableOptions for the other settings).

if (args.Length == 0)
{
    Console.WriteLine("usage: TransactHub.FlowableHarness <plan.json>");
    return 2;
}
int code;
// Traces, metrics and logs over OTLP when OTEL_* names an endpoint; the run joins the caller's trace (TRACEPARENT) (R-09-31, R-09-32).
using var telemetry = TelemetryHost.Start("transacthub-engine-flowable");
EngineTelemetry.LogSink = telemetry.Sink(EngineTelemetry.SourceName);
try
{
    var input = (JsonObject)J.Parse(await File.ReadAllTextAsync(args[0]))!;
    using var rest = FlowableRest.FromEnvironment() ?? throw new InvalidOperationException("Flowable runs on its REST server: set TH_FLOWABLE_URL (for example http://127.0.0.1:8080/flowable-rest)");
    var watch = System.Diagnostics.Stopwatch.StartNew();
    using var run = EngineTelemetry.StartRun("flowable", input);
    var obs = await new FlowableBinding(input, rest, options: FlowableOptions.FromEnvironment()).Run();
    EngineTelemetry.Observed(input, obs, watch.Elapsed);
    // What the binding did and noticed, for the deploy runner's log.
    foreach (var l in J.Strings(J.P(J.P(obs, "engine"), "log"))) Console.Error.WriteLine(l);
    foreach (var n in J.Strings(J.P(obs, "notes"))) Console.Error.WriteLine($"note: {n}");
    Console.Out.WriteLine("OBSERVATION " + J.Str(obs));
    code = 0;
}
catch (Exception e)
{
    Console.Out.WriteLine("ERROR " + JsonSerializer.Serialize(e.ToString()));
    code = 1;
}
await Console.Out.FlushAsync();
return code;
