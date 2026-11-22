using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace TransactHub.Store;

/// <summary>
/// The intake switch of a bank's scheme (R-09-62): paused or open, as insert-only facts on the switch's own chain
/// (<c>intake/&lt;tenant&gt;/&lt;scheme&gt;</c>, kind <c>intake</c>, code <c>paused</c> or <c>resumed</c>). The runtime reads the latest
/// when it accepts a payment: while paused, a new payment of that scheme for that bank is refused (the front door may keep it
/// and offer it again), and a payment already accepted runs on.
/// </summary>
public static class IntakeSwitch
{
    public const string Kind = "intake";

    public static string Subject(string tenant, string scheme) => $"intake/{tenant}/{scheme}";

    public static async Task Set(StoreDbContext db, TimeProvider clock, string tenant, string scheme, bool pause, string actor, string? why, CancellationToken ct = default) =>
        await new FactLog(db, clock).Append(Subject(tenant, scheme), Kind, new NewFact(Kind, pause ? "paused" : "resumed",
            new JsonObject { ["tenant"] = tenant, ["scheme"] = scheme, ["why"] = why }, Actor: actor), ct);

    public static async Task<bool> Paused(StoreDbContext db, string tenant, string scheme, CancellationToken ct = default)
    {
        var subject = Subject(tenant, scheme);
        return await db.Facts.AsNoTracking().Where(f => f.SubjectId == subject && f.Kind == Kind).OrderByDescending(f => f.Seq).Select(f => f.Code).FirstOrDefaultAsync(ct) == "paused";
    }
}

/// <summary>A payment refused at intake because its scheme's intake is paused for the bank (R-09-62).</summary>
public sealed class IntakePaused(string tenant, string scheme) : InvalidOperationException($"Intake of {scheme} is paused for {tenant}: the payment is not accepted; offer it again when intake is resumed")
{
    public string Code => "ops.IntakePaused";
}
