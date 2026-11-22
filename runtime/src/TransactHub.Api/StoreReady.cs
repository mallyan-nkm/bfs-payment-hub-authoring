using Microsoft.Extensions.Diagnostics.HealthChecks;
using TransactHub.Store;

namespace TransactHub.Api;

/// <summary>The release API is ready when its store answers (R-09-28). The answer names no server and no error text.</summary>
public sealed class StoreReady(StoreDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("the store does not answer");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("the store does not answer");
        }
    }
}
