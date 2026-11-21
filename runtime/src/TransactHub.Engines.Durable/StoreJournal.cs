using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Engines;
using TransactHub.Model;
using TransactHub.Store;

namespace TransactHub.Engines.Durable;

/// <summary>
/// The claim check of an engine that keeps its payloads in memory (the DTFx and Elsa harnesses), journalled
/// to the store: every create, staged write, commit, fork and copy becomes a fact on the payload's chain
/// before the in-memory store changes, and a root's payments are accepted into the ISO 20022 model. What
/// the engine ran on can then be rebuilt from the database alone (R-09-10, plan N4).
/// Installed for a process by <see cref="InstallFromEnvironment"/> when TH_STORE_PROVIDER is set.
/// </summary>
public sealed class StoreJournal(StoreOptions options, TimeProvider? clock = null) : IClaimCheckJournal
{
    readonly object gate = new();
    readonly List<ClaimCheckStore> stores = new();
    /// <summary>What was accepted into the store, by payload reference.</summary>
    public readonly Dictionary<string, (string subjectId, string kind, int payments)> Accepted = new();

    /// <summary>Installs a journal on every claim-check store this process makes, from TH_STORE_PROVIDER and TH_STORE_CONNECTION; migrates the database first.</summary>
    public static StoreJournal? InstallFromEnvironment()
    {
        var p = Environment.GetEnvironmentVariable("TH_STORE_PROVIDER");
        var c = Environment.GetEnvironmentVariable("TH_STORE_CONNECTION");
        if (string.IsNullOrEmpty(p) || string.IsNullOrEmpty(c)) return null;
        var o = new StoreOptions(Enum.Parse<StoreProvider>(p, true), c);
        using (var db = StoreDbContext.Open(o)) db.Database.Migrate();
        var j = new StoreJournal(o);
        ClaimCheckStore.DefaultJournal = j;
        return j;
    }

    // The engines call the claim check from their own threads and expect it to finish before they go on;
    // a fact is written before the in-memory store changes, so the database is never behind the engine.
    void Fact(string r, string code, JsonObject data, string? release = null)
    {
        lock (gate)
        {
            using var db = StoreDbContext.Open(options);
            new FactLog(db, clock).Append(r, DurableClaimCheck.Kind, new NewFact("payload", code, data, release, "claim-check")).GetAwaiter().GetResult();
        }
    }

    public void Attached(ClaimCheckStore store) { lock (gate) stores.Add(store); }

    public void Created(string reff, JsonObject payload, JsonObject? accept)
    {
        Fact(reff, "create", new JsonObject { ["payload"] = J.Clone(payload) });
        if (accept is null) return;
        var a = new AcceptInfo(J.S(accept, "tenant") ?? "tenant", J.S(accept, "scheme") ?? "", J.S(accept, "direction") == "inward" ? Direction.Inward : Direction.Outward, J.S(accept, "release") ?? "");
        lock (gate)
        {
            using var db = StoreDbContext.Open(options);
            var store = new PaymentStore(db, clock);
            var log = new FactLog(db, clock);
            if (CanonicalMapper.ToBulk(payload) is { } bulk)
            {
                var b = store.AcceptBulk(a.Tenant, a.Scheme, a.Direction, bulk, a.ReleaseId).GetAwaiter().GetResult();
                Accepted[reff] = (b.BulkId, "bulk", b.PaymentIds.Count);
                log.Append(reff, DurableClaimCheck.Kind, new NewFact("payload", "accepted", new JsonObject { ["bulk"] = b.BulkId, ["payments"] = b.PaymentIds.Count }, a.ReleaseId, "claim-check")).GetAwaiter().GetResult();
            }
            else if (CanonicalMapper.ToPayment(payload, null, 0) is { } t)
            {
                var id = store.AcceptPayment(a.Tenant, a.Scheme, a.Direction, t, a.ReleaseId).GetAwaiter().GetResult();
                Accepted[reff] = (id, "payment", 1);
                log.Append(reff, DurableClaimCheck.Kind, new NewFact("payload", "accepted", new JsonObject { ["payment"] = id }, a.ReleaseId, "claim-check")).GetAwaiter().GetResult();
            }
        }
    }

    public void Staged(string token, string reff, IReadOnlyList<(string path, JsonNode? value)> writes) =>
        Fact(reff, "staged", new JsonObject { ["token"] = token, ["writes"] = new JsonArray(writes.Select(w => (JsonNode?)new JsonObject { ["path"] = w.path, ["value"] = J.Clone(w.value) }).ToArray()) });

    public void Committed(string token, string reff) => Fact(reff, "commit", new JsonObject { ["token"] = token });

    public void Forked(string from, string to, JsonObject payload) => Fact(to, "fork", new JsonObject { ["from"] = from, ["payload"] = J.Clone(payload) });

    public void Copied(string from, string to, JsonObject values) => Fact(to, "copy", new JsonObject { ["from"] = from, ["values"] = J.Clone(values) });

    /// <summary>The store after the run: what it accepted, and every payload rebuilt from its facts, its chain checked, compared with what the engine held.</summary>
    public JsonObject? Report()
    {
        ClaimCheckStore[] live;
        lock (gate) live = stores.ToArray();
        return StoreReport.Of(options, r =>
        {
            // The store that holds the reference (a harness makes one; a check may make more).
            foreach (var s in live) { var p = s.Payload(r); if (p.Count > 0) return p; }
            return new JsonObject();
        }).GetAwaiter().GetResult();
    }
}

/// <summary>What the store holds after a run, the same for every engine: the observation's engine.store.</summary>
public static class StoreReport
{
    public static async Task<JsonObject> Of(StoreOptions o, Func<string, JsonObject> live)
    {
        await using var db = StoreDbContext.Open(o);
        var facts = new FactLog(db);
        var refs = await db.Facts.Where(f => f.SubjectKind == DurableClaimCheck.Kind).Select(f => f.SubjectId).Distinct().ToListAsync();
        var chains = new JsonArray();
        var allOk = true;
        foreach (var r in refs.Order(StringComparer.Ordinal))
        {
            var v = await facts.Verify(r);
            var rebuilt = await DurableClaimCheck.Load(o, r);
            var same = CanonicalJson.Of(rebuilt) == CanonicalJson.Of(live(r));
            allOk &= v.Ok && same;
            chains.Add(new JsonObject { ["ref"] = r, ["facts"] = v.Facts, ["chain"] = v.Ok, ["rebuilt"] = same });
        }
        return new JsonObject
        {
            ["provider"] = o.Provider.ToString(),
            ["bulks"] = await db.Bulks.CountAsync(),
            ["payments"] = await db.Payments.CountAsync(),
            ["parties"] = await db.Parties.CountAsync(),
            ["payloads"] = chains, ["ok"] = allOk,
        };
    }
}
