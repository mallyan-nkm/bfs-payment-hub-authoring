using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace TransactHub.Telemetry;

/// <summary>OpenTelemetry and the health endpoints for a runtime host that serves HTTP (the release API) (R-09-23, R-09-28).</summary>
public static class WebTelemetry
{
    /// <summary>Tag of the checks that decide readiness; liveness runs none.</summary>
    public const string Ready = "ready";

    /// <summary>
    /// Traces, metrics and logs as <see cref="TelemetryHost.Start"/> sets them up, for an ASP.NET Core host, with
    /// its requests as spans and the masking processors first. A test passes its own exporters through the hooks.
    /// </summary>
    public static IServiceCollection AddTransactHubTelemetry(this IServiceCollection services, string serviceName,
        Action<TracerProviderBuilder>? traces = null, Action<MeterProviderBuilder>? metrics = null)
    {
        var export = TelemetryHost.ExportConfigured;
        services.AddHealthChecks();
        if (!export && traces is null && metrics is null) return services;
        services.AddOpenTelemetry()
            .ConfigureResource(r => TelemetryHost.Describe(r, serviceName))
            .WithTracing(t =>
            {
                t.AddSource(TelemetryHost.Sources).AddAspNetCoreInstrumentation(o => o.Filter = c => !c.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation().AddProcessor(new MaskingSpanProcessor());
                if (export) t.AddOtlpExporter();
                traces?.Invoke(t);
            })
            .WithMetrics(m =>
            {
                m.AddMeter(TelemetryHost.Meters).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation();
                if (export) m.AddOtlpExporter();
                metrics?.Invoke(m);
            })
            .WithLogging(l =>
            {
                l.AddProcessor(new MaskingLogProcessor());
                if (export) l.AddOtlpExporter();
            }, o => { o.IncludeFormattedMessage = true; o.ParseStateValues = true; });
        return services;
    }

    /// <summary>
    /// GET /health/live (the process answers) and GET /health/ready (every check tagged ready passes, the store
    /// among them). Both answer JSON with the status of each check and no detail of the failure.
    /// </summary>
    public static IEndpointRouteBuilder MapTransactHubHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = Write });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(Ready), ResponseWriter = Write });
        return app;
    }

    static Task Write(HttpContext c, HealthReport r)
    {
        c.Response.ContentType = "application/json";
        return c.Response.WriteAsJsonAsync(new
        {
            status = r.Status.ToString().ToLowerInvariant(),
            checks = r.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString().ToLowerInvariant()),
        });
    }
}
