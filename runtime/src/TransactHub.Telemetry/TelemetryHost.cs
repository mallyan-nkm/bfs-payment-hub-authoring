using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace TransactHub.Telemetry;

/// <summary>
/// OpenTelemetry for a runtime host (R-13-01, I-66). Traces, metrics and logs go to an OTLP endpoint named only
/// by the standard variables (OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_PROTOCOL, OTEL_SERVICE_NAME,
/// OTEL_RESOURCE_ATTRIBUTES, OTEL_SDK_DISABLED and the per-signal ones); with none set, nothing is exported
/// and nothing listens, so a host costs what it did before. Every signal passes the masking processors first
/// (R-13-05). A collector that is down never stops the host: the exporters drop what they cannot send.
/// </summary>
public sealed class TelemetryHost : IDisposable
{
    /// <summary>Every source of spans the runtime has: the engine bindings, the store, the hosts, Temporal's interceptor.</summary>
    public static readonly string[] Sources = ["TransactHub.Engine", "TransactHub.Store", RuntimeTelemetry.SourceName, "Temporalio.Extensions.OpenTelemetry.*"];
    /// <summary>Every meter: the same, and Temporal's SDK metrics (through Temporalio.Extensions.DiagnosticSource).</summary>
    public static readonly string[] Meters = ["TransactHub.Engine", "TransactHub.Store", RuntimeTelemetry.SourceName, RuntimeTelemetry.TemporalMeterName];

    readonly TracerProvider? tracer;
    readonly MeterProvider? meter;
    readonly ILoggerFactory loggers;

    public TracerProvider? Tracer => tracer;
    public MeterProvider? MeterProvider => meter;
    public ILoggerFactory Loggers => loggers;

    /// <summary>Whether the environment names an OTLP endpoint and does not switch the SDK off.</summary>
    public static bool ExportConfigured
    {
        get
        {
            if (string.Equals(Environment.GetEnvironmentVariable("OTEL_SDK_DISABLED"), "true", StringComparison.OrdinalIgnoreCase)) return false;
            return new[] { "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT" }
                .Any(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)));
        }
    }

    public static ResourceBuilder Resource(string serviceName) => Describe(ResourceBuilder.CreateDefault(), serviceName);

    /// <summary>The service a host is: OTEL_SERVICE_NAME when set (the default resource has read it), else the host's own name.</summary>
    public static ResourceBuilder Describe(ResourceBuilder r, string serviceName)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME"))) r.AddService(serviceName, serviceVersion: "1.0.0");
        return r.AddAttributes([new("th.host", serviceName)]);
    }

    TelemetryHost(TracerProvider? tracer, MeterProvider? meter, ILoggerFactory loggers)
    {
        this.tracer = tracer;
        this.meter = meter;
        this.loggers = loggers;
    }

    /// <summary>
    /// Start telemetry for a process. Exports over OTLP when <see cref="ExportConfigured"/>; a test adds its own
    /// exporters through the three hooks (after the masking processors, so it sees what an exporter would).
    /// </summary>
    public static TelemetryHost Start(string serviceName,
        Action<TracerProviderBuilder>? traces = null, Action<MeterProviderBuilder>? metrics = null, Action<OpenTelemetryLoggerOptions>? logs = null)
    {
        var export = ExportConfigured;
        if (!export && traces is null && metrics is null && logs is null)
            return new TelemetryHost(null, null, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var resource = Resource(serviceName);
        var tb = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).AddSource(Sources).AddHttpClientInstrumentation().AddProcessor(new MaskingSpanProcessor());
        if (export) tb.AddOtlpExporter();
        traces?.Invoke(tb);
        var mb = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource).AddMeter(Meters).AddRuntimeInstrumentation().AddHttpClientInstrumentation();
        if (export) mb.AddOtlpExporter();
        metrics?.Invoke(mb);
        var lf = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.SetResourceBuilder(resource);
            o.IncludeFormattedMessage = true;
            o.ParseStateValues = true;
            o.AddProcessor(new MaskingLogProcessor());
            if (export) o.AddOtlpExporter();
            logs?.Invoke(o);
        }));
        var host = new TelemetryHost(tb.Build(), mb.Build(), lf);
        // A process that ends by Environment.Exit still sends what it holds.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => host.Flush();
        return host;
    }

    /// <summary>A sink for code that cannot take an ILogger (the shared engine code): level, template, values.</summary>
    public Action<int, string, object?[]> Sink(string category)
    {
        var logger = loggers.CreateLogger(category);
        return (level, template, values) =>
        {
            var l = level switch { 0 => LogLevel.Debug, 1 => LogLevel.Information, 2 => LogLevel.Warning, _ => LogLevel.Error };
#pragma warning disable CA2254 // the template is a constant at each call site
            logger.Log(l, template, values);
#pragma warning restore CA2254
        };
    }

    public void Flush()
    {
        try
        {
            tracer?.ForceFlush(5000);
            meter?.ForceFlush(5000);
        }
        catch
        {
            // Telemetry never stops the host.
        }
    }

    bool disposed;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Flush();
        loggers.Dispose();
        tracer?.Dispose();
        meter?.Dispose();
    }
}

/// <summary>The runtime hosts' own signals (the API, the harnesses): release activations and the names of Temporal's meter.</summary>
public static class RuntimeTelemetry
{
    public const string SourceName = "TransactHub.Runtime";
    public const string TemporalMeterName = "Temporalio";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
    public static readonly Meter Meter = new(SourceName, "1.0.0");
    public static readonly Counter<long> ReleasesStored = Meter.CreateCounter<long>("th.release.stored", "{release}", "Releases stored by the release API (a repeat of a stored one is not counted)");
    public static readonly Counter<long> Activations = Meter.CreateCounter<long>("th.release.activations", "{activation}", "Activations recorded by the release API, by environment and scope");
}
