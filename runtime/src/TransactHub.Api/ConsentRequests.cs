using System.Text.Json.Nodes;
using TransactHub.Store;

namespace TransactHub.Api;

/// <summary>
/// A change of the system the release API may not carry out by itself, asked for in the service console's book of actions
/// (spec R-09-77, R-09-111): the same <c>action.requested</c> fact on the console's chain that a service engineer's request is,
/// so a bank approver, another person, consents to it in the service console, and it is carried out there on consent. The
/// pipeline is the one who asked; it may not consent.
/// </summary>
public static class ConsentRequests
{
    /// <summary>The service console's chain and fact kind (the service console's <c>ServiceAudit</c> uses the same).</summary>
    public const string Subject = "service-console", Kind = "service";

    /// <summary>The action that puts an installed configuration release in force (R-09-111).</summary>
    public const string PutInForce = "put-in-force";

    /// <summary>Records the request; returns the action's id, which the service console's consent names.</summary>
    public static async Task<string> Ask(StoreDbContext db, TimeProvider clock, string action, string target, string why, string actor, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        await new FactLog(db, clock).Append(Subject, Kind, new NewFact(Kind, "action.requested", new JsonObject
        {
            ["action"] = id, ["name"] = action, ["target"] = target, ["parameters"] = null, ["risky"] = true, ["path"] = "built", ["why"] = why, ["by"] = actor, ["via"] = "release-api",
        }, Actor: actor), ct);
        return id;
    }

    /// <summary>The id of a request for this action and target that no one has decided yet, if there is one.</summary>
    public static async Task<string?> Open(StoreDbContext db, string action, string target, CancellationToken ct = default)
    {
        var facts = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.Facts.Where(f => f.SubjectId == Subject && f.Kind == Kind && f.Code!.StartsWith("action.")).OrderBy(f => f.Seq), ct);
        var byAction = facts.Select(f => (f, d: JsonNode.Parse(f.DataJson))).GroupBy(x => (string?)x.d?["action"]);
        return byAction.Where(g => g.Count() == 1 && g.First().f.Code == "action.requested" && (string?)g.First().d?["name"] == action && (string?)g.First().d?["target"] == target)
            .Select(g => g.Key).FirstOrDefault();
    }
}
