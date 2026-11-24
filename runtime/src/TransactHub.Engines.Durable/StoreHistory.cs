using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Engines;
using TransactHub.Store;

namespace TransactHub.Engines.Durable;

/// <summary>
/// The history of a distribution's engine service in the store (Issue I-165, R-09-196): one chain per engine and distribution
/// (subject <c>&lt;engine&gt;-history:&lt;hash&gt;</c>, kind <c>engine-history</c>), insert-only and SHA-256 chained like every fact
/// (<see cref="FactLog"/>; the store refuses an update or a delete, <c>InsertOnlyGuard</c>). The service writes each fact before
/// the change it records, in the order it makes them, so the chain is always a prefix of what the service did: a service that starts
/// again folds it into the state it had. Its payloads are not on it: they are the claim check's own facts (R-09-10), which
/// <see cref="RestorePayloads"/> folds back the same way <see cref="DurableClaimCheck"/> does.
/// </summary>
public sealed class StoreHistory(StoreOptions store, string engine, string distribution) : IEngineHistory
{
    public const string Kind = "engine-history";

    /// <summary>The chain of an engine's distribution: its subject id fits the store's 64 characters whatever the distribution's id.</summary>
    public static string SubjectOf(string engine, string distribution) =>
        $"{engine}-history:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(distribution)))[..40]}";

    public string Subject { get; } = SubjectOf(engine, distribution);
    public string Distribution => distribution;

    /// <summary>Installs the store as every serving harness's history in this process.</summary>
    public static void Install(StoreOptions store) => EngineHistory.Open = (engine, distribution) => new StoreHistory(store, engine, distribution);

    public IReadOnlyList<(string code, JsonObject data)> Load()
    {
        using var db = StoreDbContext.Open(store);
        var facts = new FactLog(db).Of(Subject).GetAwaiter().GetResult()
            .Select(f => (f.Code ?? "", JsonNode.Parse(f.DataJson) as JsonObject ?? new JsonObject())).ToList();
        // A service that starts again may run again an attempt its predecessor had answered just before it stopped, and replays the
        // decisions its instances took: a step the payment's chain records already is not recorded twice (as on Flowable, I-89).
        if (facts.Count > 0) StepFacts.Current?.SkipRecorded();
        return facts;
    }

    public void Append(string code, JsonObject data)
    {
        using var db = StoreDbContext.Open(store);
        new FactLog(db).Append(Subject, Kind, new NewFact("history", code, data, Actor: $"engine:{engine}")).GetAwaiter().GetResult();
    }

    public JsonObject Verify()
    {
        using var db = StoreDbContext.Open(store);
        var v = new FactLog(db).Verify(Subject).GetAwaiter().GetResult();
        return new JsonObject { ["subject"] = Subject, ["facts"] = v.Facts, ["chain"] = v.Ok };
    }

    public bool RestorePayloads(ClaimCheckStore claim, string prefix)
    {
        using var db = StoreDbContext.Open(store);
        var facts = db.Facts.AsNoTracking().Where(f => f.SubjectKind == DurableClaimCheck.Kind && f.SubjectId.StartsWith(prefix))
            .OrderBy(f => f.SubjectId).ThenBy(f => f.Seq).ToList();
        foreach (var chain in facts.GroupBy(f => f.SubjectId))
        {
            var (payload, pending, tokens) = Fold(chain);
            claim.Restore(chain.Key, payload, pending.Select(kv => (kv.Key, kv.Value)), tokens);
        }
        return facts.Count > 0;
    }

    /// <summary>A payload's claim-check facts folded: the payload, the writes staged and not committed, and every token named.</summary>
    public static (JsonObject payload, Dictionary<string, List<(string path, JsonNode? value)>> pending, List<string> tokens) Fold(IEnumerable<FactRow> facts)
    {
        var p = new JsonObject();
        var pending = new Dictionary<string, List<(string path, JsonNode? value)>>(StringComparer.Ordinal);
        var tokens = new List<string>();
        foreach (var f in facts)
        {
            var d = (JsonObject)JsonNode.Parse(f.DataJson)!;
            switch (f.Code)
            {
                case "create":
                case "fork":
                    p = (JsonObject)J.Clone(d["payload"])!;
                    break;
                case "staged":
                    tokens.Add((string)d["token"]!);
                    pending[(string)d["token"]!] = ((JsonArray)d["writes"]!).Select(w => ((string)w!["path"]!, J.Clone(w["value"]))).ToList();
                    break;
                case "commit":
                    if (pending.Remove((string)d["token"]!, out var ws)) foreach (var (path, v) in ws) J.Set(p, path, J.Clone(v));
                    break;
                case "copy":
                    foreach (var (path, v) in (JsonObject)d["values"]!) if (v is not null) J.Set(p, path, J.Clone(v));
                    break;
            }
        }
        return (p, pending, tokens);
    }
}
