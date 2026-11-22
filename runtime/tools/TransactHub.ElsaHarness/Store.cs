using System.Runtime.CompilerServices;
using TransactHub.Engines;
using TransactHub.Engines.Durable;
using TransactHub.Telemetry;

namespace TransactHub.ElsaHarness;

static class Store
{
    /// <summary>Before the harness's Main: the store journal, when the deployment names a store, and telemetry.</summary>
    [ModuleInitializer]
    internal static void Install()
    {
        StoreJournal.InstallFromEnvironment();
        // Traces, metrics and logs over OTLP when OTEL_* names an endpoint (R-09-23). The engine runs the attempts on its
        // own threads, so each attempt span joins the caller's trace (TRACEPARENT) through EngineTelemetry's parent.
        var telemetry = TelemetryHost.Start("transacthub-engine-elsa");
        EngineTelemetry.Engine = "elsa";
        EngineTelemetry.LogSink = telemetry.Sink(EngineTelemetry.SourceName);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => telemetry.Dispose();
    }
}
