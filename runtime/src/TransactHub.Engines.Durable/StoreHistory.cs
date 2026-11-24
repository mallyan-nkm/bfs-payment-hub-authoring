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
/// <para>
/// The chain is never trimmed, so every <see cref="CheckpointEvery"/> facts the service writes a checkpoint (R-09-239, Issue I-301):
/// one fact that holds, in their order, the facts of the payments that have not ended, without those of the payments whose end the
/// host recorded (<c>reported</c>), their starts, their instances, messages, episodes and operations, and without the service's own
/// openings. A service that starts again reads the chain from its last checkpoint only, so its start costs what its open payments
/// hold, not what the distribution ever ran; and it folds what it reads as before, the one way a service is rebuilt. Chosen over a
/// snapshot of each engine's state: a second way to rebuild a service would have to give the same service as the fold, on both engines.
/// </para>
/// </summary>
public sealed class StoreHistory(StoreOptions store, string engine, string distribution) : IEngineHistory
{
    public const string Kind = "engine-history";

    /// <summary>The chain of an engine's distribution: its subject id fits the store's 64 characters whatever the distribution's id.</summary>
    public static string SubjectOf(string engine, string distribution) =>
        $"{engine}-history:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(distribution)))[..40]}";

    public string Subject { get; } = SubjectOf(engine, distribution);
    public string Distribution => distribution;
    public const string CheckpointCode = "checkpoint";

    /// <summary>
    /// How many facts the service writes between checkpoints (R-09-239): the host's setting, given as <c>TH_HISTORY_CHECKPOINT_EVERY</c>;
    /// 0 writes none.
    /// </summary>
    public int CheckpointEvery { get; init; } =
        int.TryParse(Environment.GetEnvironmentVariable("TH_HISTORY_CHECKPOINT_EVERY"), out var n) && n > 0 ? n : 0;

    /// <summary>What the last <see cref="Load"/> read: the checkpoint it started from (its sequence number, 0 for none) and the facts.</summary>
    public (int From, int Rows) Loaded { get; private set; }
    /// <summary>The checkpoints this service wrote.</summary>
    public int Checkpoints { get; private set; }

    readonly object gate = new();
    List<(string code, JsonObject data)> live = new();
    int since;

    /// <summary>Installs the store as every serving harness's history in this process.</summary>
    public static void Install(StoreOptions store) => EngineHistory.Open = (engine, distribution) => new StoreHistory(store, engine, distribution);

    public IReadOnlyList<(string code, JsonObject data)> Load()
    {
        using var db = StoreDbContext.Open(store);
        // From the last checkpoint only (R-09-239): what it holds, then every fact after it.
        var from = db.Facts.AsNoTracking().Where(f => f.SubjectId == Subject && f.Code == CheckpointCode).OrderByDescending(f => f.Seq).Select(f => (int?)f.Seq).FirstOrDefault() ?? 0;
        var rows = db.Facts.AsNoTracking().Where(f => f.SubjectId == Subject && f.Seq >= from).OrderBy(f => f.Seq).Select(f => new { f.Code, f.DataJson }).ToList();
        var facts = new List<(string code, JsonObject data)>();
        foreach (var r in rows)
        {
            var d = JsonNode.Parse(r.DataJson) as JsonObject ?? new JsonObject();
            if (r.Code == CheckpointCode)
                foreach (var x in (d["facts"] as JsonArray ?? []).OfType<JsonObject>()) facts.Add(((string?)x["code"] ?? "", x["data"] as JsonObject ?? new JsonObject()));
            else facts.Add((r.Code ?? "", d));
        }
        lock (gate)
        {
            live = facts.Select(f => (f.code, (JsonObject)f.data.DeepClone())).ToList();
            since = rows.Count(r => r.Code != CheckpointCode);
            Loaded = (from, rows.Count);
        }
        // A service that starts again may run again an attempt its predecessor had answered just before it stopped, and replays the
        // decisions its instances took: a step the payment's chain records already is not recorded twice (as on Flowable, I-89).
        if (rows.Count > 0) StepFacts.Current?.SkipRecorded();
        return facts;
    }

    public void Append(string code, JsonObject data)
    {
        lock (gate)
        {
            using (var db = StoreDbContext.Open(store))
                new FactLog(db).Append(Subject, Kind, new NewFact("history", code, data, Actor: $"engine:{engine}")).GetAwaiter().GetResult();
            live.Add((code, (JsonObject)data.DeepClone()));
            if (CheckpointEvery > 0 && ++since >= CheckpointEvery) Checkpoint();
        }
    }

    /// <summary>The open payments' facts, in their order, as one fact on the chain; a later start reads from it (R-09-239).</summary>
    void Checkpoint()
    {
        var kept = Compact(live);
        var data = new JsonObject
        {
            ["facts"] = new JsonArray(kept.Select(f => (JsonNode?)new JsonObject { ["code"] = f.code, ["data"] = f.data.DeepClone() }).ToArray()),
            ["kept"] = kept.Count, ["dropped"] = live.Count - kept.Count,
        };
        using (var db = StoreDbContext.Open(store))
            new FactLog(db).Append(Subject, Kind, new NewFact("history", CheckpointCode, data, Actor: $"engine:{engine}")).GetAwaiter().GetResult();
        live = kept;
        since = 0;
        Checkpoints++;
    }

    /// <summary>The payment's root of a history fact: its <c>rootId</c>, else the root of its instance (<c>instance</c> or <c>id</c>).</summary>
    static string? RootOf(JsonObject d) =>
        ((string?)d["rootId"] ?? (string?)d["instance"] ?? (string?)d["id"])?.Split('/')[0];

    /// <summary>
    /// A service's facts without those of the payments whose end the host recorded and without the service's openings, in their order
    /// (R-09-239). An activity's answer of no payment's (<c>activity-done</c>) stays only while an episode kept scheduled it.
    /// </summary>
    public static List<(string code, JsonObject data)> Compact(IReadOnlyList<(string code, JsonObject data)> facts)
    {
        var reported = facts.Where(f => f.code == "reported").Select(f => RootOf(f.data)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        bool Open(JsonObject d) => RootOf(d) is not string r || !reported.Contains(r);
        var scheduled = facts.Where(f => f.code == "episode" && Open(f.data))
            .SelectMany(f => (f.data["activities"] as JsonArray ?? []).OfType<JsonObject>().Select(a => a["seq"]?.ToString()))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        return facts.Where(f => f.code switch
        {
            "opened" or "restarted" or "reported" or CheckpointCode => false,
            "activity-done" => f.data["activity"]?.ToString() is string a && scheduled.Contains(a),
            _ => Open(f.data),
        }).Select(f => (f.code, (JsonObject)f.data.DeepClone())).ToList();
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
