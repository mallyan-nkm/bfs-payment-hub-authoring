using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace TransactHub.Store;

/// <summary>
/// An engine's own failure of a payment as a fact on its chain (R-09-70, Issues I-103 and I-126): kind <c>engine</c>, code
/// <c>failed</c>, beside <c>engine started</c>, with the error dictionary's code of that engine's failure, so the service console
/// shows it with its runbook and the operations console on the payment's timeline, without the engine's own console. The reason
/// is the engine's message, cut short; a host passes ids and engine words only, never a party's value.
/// </summary>
public static class EngineFailures
{
    /// <summary>The dictionary's code of an engine's failure of an instance (p.errors.platform.v1).</summary>
    public static string CodeOf(string engine) => engine switch
    {
        "temporal" => "temporal.WorkflowTaskFailed",
        "dtfx" => "dtfx.OrchestrationFailed",
        "elsa" => "elsa.WorkflowFaulted",
        "flowable" => "flowable.DeadLetterJob",
        _ => "Failure",
    };

    public static async Task<FactRow?> Record(StoreDbContext db, string subjectId, string engine, string? instance, string? reason, TimeProvider? clock = null, string? distribution = null, CancellationToken ct = default)
    {
        var kind = await db.Bulks.AsNoTracking().AnyAsync(b => b.Id == subjectId, ct) ? "bulk"
            : await db.Payments.AsNoTracking().AnyAsync(p => p.Id == subjectId, ct) ? "payment" : null;
        if (kind is null) return null;
        var code = CodeOf(engine);
        var text = (reason ?? "").ReplaceLineEndings(" ");
        var data = new JsonObject
        {
            ["engine"] = engine, ["instance"] = instance, ["code"] = code, ["error"] = code,
            ["reason"] = text.Length > 300 ? text[..300] : text, ["distribution"] = distribution,
        };
        return await new FactLog(db, clock).Append(subjectId, kind, new NewFact("engine", "failed", data, Actor: $"engine:{engine}"), ct);
    }
}
