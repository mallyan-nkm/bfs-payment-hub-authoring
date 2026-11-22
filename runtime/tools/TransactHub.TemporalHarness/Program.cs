using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Temporalio.Extensions.DiagnosticSource;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Runtime;
using Temporalio.Testing;
using TransactHub.Engine.Temporal;
using TransactHub.Engines;
using TransactHub.Telemetry;

// The Temporal harness: `dotnet TransactHub.TemporalHarness.dll <plan.json>`. It takes the plan the
// studio's kit runner and deploy runner write for every engine, runs it on Temporal and prints
// OBSERVATION <json>, as the DTFx and Elsa harnesses do. With TH_TEMPORAL_ADDRESS it runs on that
// server in real time; without, on Temporal's time-skipping test server.
//
// `--load <plan.json>`: the runtime's load harness (runtime/load, R-13-09). The plan carries many runs of one
// deployment; it prints LOAD <json>, what each run took and how it ended.
//
// Telemetry (R-13-01, R-13-02): with OTEL_EXPORTER_OTLP_ENDPOINT set, traces, metrics and logs go there over OTLP.
// The run joins the caller's trace (TRACEPARENT, as the deploy runner sets it); Temporal's interceptor carries
// it through every workflow, activity and signal; Temporal's SDK metrics are a meter of their own.

if (args.Length == 0)
{
    Console.WriteLine("usage: TransactHub.TemporalHarness [--load] <plan.json>");
    return 2;
}
var load = args[0] == "--load";
var planFile = load ? args[1] : args[0];
using var telemetry = TelemetryHost.Start("transacthub-engine-temporal");
EngineTelemetry.LogSink = telemetry.Sink(EngineTelemetry.SourceName);
EngineTelemetry.Engine = "temporal";
var runtime = new TemporalRuntime(new TemporalRuntimeOptions(new TelemetryOptions
{
    Metrics = new MetricsOptions { CustomMetricMeter = new CustomMetricMeter(new Meter(RuntimeTelemetry.TemporalMeterName)) },
}));
var interceptors = new[] { new TracingInterceptor() };
var input = (JsonObject)J.Parse(await File.ReadAllTextAsync(planFile))!;
int code;
WorkflowEnvironment? env = null;
try
{
    env = Environment.GetEnvironmentVariable("TH_TEMPORAL_ADDRESS") is { Length: > 0 } address
        ? new WorkflowEnvironment(await Temporalio.Client.TemporalClient.ConnectAsync(new(address) { Runtime = runtime, Interceptors = interceptors }))
        : await ScenarioRunner.StartTestServer(runtime, interceptors);
    if (load)
    {
        var result = await new LoadRunner(input).Run(env);
        Console.Out.WriteLine("LOAD " + J.Str(result));
    }
    else
    {
        var watch = Stopwatch.StartNew();
        JsonObject obs;
        using (EngineTelemetry.StartRun("temporal", input))
        {
            obs = await new ScenarioRunner(input).Run(env);
            EngineTelemetry.Observed(input, obs, watch.Elapsed);
        }
        Console.Out.WriteLine("OBSERVATION " + J.Str(obs));
    }
    code = 0;
}
catch (Exception e)
{
    Console.Out.WriteLine("ERROR " + JsonSerializer.Serialize(e.ToString()));
    code = 1;
}
await Console.Out.FlushAsync();
if (env is not null) { try { await env.ShutdownAsync(); } catch { } }
telemetry.Flush();
return code;
