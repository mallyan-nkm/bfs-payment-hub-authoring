using System.Text.Json.Nodes;
using TransactHub.EngineHost;
using TransactHub.Engines;

// The long-running engine host (Issues I-114, I-127; R-09-66 to R-09-68).
//
//   TransactHub.EngineHost supervise --engine <temporal|dtfx|elsa|flowable> --bank <bank> --environment <env> --plans <dir> [--urls <url>]
//     The supervisor of one engine for one bank and environment. It reads the bank's distributions and their states from the
//     store (TH_STORE_PROVIDER, TH_STORE_CONNECTION) every TH_HOST_POLL (default PT2S), starts a host for each distribution that
//     is installed, active, draining or drained (its plan is <dir>/<id>.json, the distribution's copy), tells the active one's
//     host that it is current, records a draining distribution drained once no payment pinned to it is open, and stops a removed
//     one's host, which unregisters it from the engine. A payment it is given is pinned at acceptance and run by the host of its
//     distribution: POST /payments { paymentId, context }, GET /payments/<id>, GET /distributions.
//
//   TransactHub.EngineHost serve --engine <e> --distribution <dist/bank@id> --plan <file> --bank <bank> --environment <env> [--urls <url>]
//     The host of one distribution, as the supervisor starts it. On Temporal (TH_TEMPORAL_ADDRESS, a real server) it runs the
//     distribution's workers as one version of the bank's worker deployment, its build id the distribution's id, for as long as it
//     runs; on DTFx, Elsa and Flowable it runs each payment on the engine's runtime harness, the engine naming its orchestrations,
//     definitions or deployment after the distribution. It prints LISTENING <url> once it serves.

static Dictionary<string, string> Options(string[] a)
{
    var o = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i + 1 < a.Length; i += 2) o[a[i].TrimStart('-')] = a[i + 1];
    return o;
}

if (args.Length == 0 || args[0] is not ("supervise" or "serve"))
{
    Console.Error.WriteLine("usage: TransactHub.EngineHost supervise --engine e --bank b --environment env --plans dir [--urls url] | serve --engine e --distribution d --plan file --bank b --environment env [--urls url]");
    return 2;
}
var opts = Options(args);
// Temporal is the default engine (owner's decision of 23 November 2026, R-09-79): on the Temporal SQL Server plugin in a
// deployment. DTFx, Elsa and Flowable are started by naming them. The bank defaults to the runtime's one bank (TH_BANK, R-09-76),
// and a supervisor for another bank is refused.
opts.TryAdd("engine", "temporal");
if (Environment.GetEnvironmentVariable("TH_BANK") is { Length: > 0 } runtimeBank)
{
    if (opts.TryGetValue("bank", out var asked) && asked != runtimeBank)
    {
        Console.Error.WriteLine($"engine host: this runtime serves {runtimeBank} only (TH_BANK); a host for {asked} is refused (R-09-76)");
        return 2;
    }
    opts["bank"] = runtimeBank;
}
var store = HostStore.FromEnvironment() ?? throw new InvalidOperationException("The engine host keeps its state in the store: set TH_STORE_PROVIDER and TH_STORE_CONNECTION");
// Its breakers' moves in the store, so the service console's health shows an open one (R-09-156, Issue I-255).
TransactHub.Ports.CircuitBreakers.Shared.Journal = new TransactHub.Operations.StoreBreakerJournal(store, $"{Environment.MachineName}/engine-host");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
try
{
    if (args[0] == "supervise")
    {
        var s = new Supervisor(store, opts["engine"], opts["bank"], opts["environment"], opts["plans"]);
        await s.Run(opts.GetValueOrDefault("urls") ?? "http://127.0.0.1:0/", stop.Token);
    }
    else
    {
        var plan = (JsonObject)J.Parse(await File.ReadAllTextAsync(opts["plan"]))!;
        DistributionHost host = opts["engine"] == "temporal"
            ? new TemporalDistributionHost(store, opts["distribution"], plan, opts["bank"], opts["environment"])
            : new HarnessDistributionHost(store, opts["engine"], opts["distribution"], plan, opts["bank"], opts["environment"]);
        await host.Run(opts.GetValueOrDefault("urls") ?? "http://127.0.0.1:0/", stop.Token);
    }
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception e)
{
    Console.Error.WriteLine($"engine host: {e}");
    return 1;
}
