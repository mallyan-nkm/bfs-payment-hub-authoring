using System.Text.Json;
using System.Text.Json.Nodes;
using Temporalio.Testing;
using TransactHub.Engine.Temporal;
using TransactHub.Engines;

// The Temporal harness: `dotnet TransactHub.TemporalHarness.dll <plan.json>`. It takes the plan the
// studio's kit runner and deploy runner write for every engine, runs it on Temporal and prints
// OBSERVATION <json>, as the DTFx and Elsa harnesses do. With TH_TEMPORAL_ADDRESS it runs on that
// server in real time; without, on Temporal's time-skipping test server.

if (args.Length == 0)
{
    Console.WriteLine("usage: TransactHub.TemporalHarness <plan.json>");
    return 2;
}
var input = (JsonObject)J.Parse(await File.ReadAllTextAsync(args[0]))!;
int code;
WorkflowEnvironment? env = null;
try
{
    env = Environment.GetEnvironmentVariable("TH_TEMPORAL_ADDRESS") is { Length: > 0 } address
        ? new WorkflowEnvironment(await Temporalio.Client.TemporalClient.ConnectAsync(new(address)))
        : await ScenarioRunner.StartTestServer();
    var obs = await new ScenarioRunner(input).Run(env);
    Console.Out.WriteLine("OBSERVATION " + J.Str(obs));
    code = 0;
}
catch (Exception e)
{
    Console.Out.WriteLine("ERROR " + JsonSerializer.Serialize(e.ToString()));
    code = 1;
}
await Console.Out.FlushAsync();
if (env is not null) { try { await env.ShutdownAsync(); } catch { } }
return code;
