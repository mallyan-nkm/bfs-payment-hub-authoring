using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations.Registers;

// The bank's registers (R-05-62 to R-05-64; owner's decisions of 23 November 2026). Bank customer data, starting with the
// corporate registration, kept in the runtime's store only. A register kind is declared (a register-kind document: its key, its
// fields with their types, their ISO 20022 homes, which are personal and how they are masked, and which a record is looked up
// by), never a screen: the console and the APIs render any kind the same way. Every change is a maker's draft that takes effect
// only when a different person with the bank approver's role consents (R-05-63); each draft, consent and refusal is also a fact
// on the change's own chain.

/// <summary>A field of a register kind.</summary>
public sealed record RegisterField(string Id, string Title, string Type, bool Required, bool Personal, string? Mask, bool Lookup, string? Iso, string? References, string? Pattern);

/// <summary>A declared register kind.</summary>
public sealed record RegisterKind(string Name, string Title, string Description, string Key, string TitleField, IReadOnlyList<RegisterField> Fields, JsonObject Doc)
{
    public RegisterField? Field(string id) => Fields.FirstOrDefault(f => f.Id == id);
    public IEnumerable<RegisterField> Lookups => Fields.Where(f => f.Lookup);

    public static RegisterKind Parse(JsonObject d)
    {
        if ((string?)d["kind"] != "register-kind") throw new InvalidOperationException("A register kind declaration is of kind register-kind");
        var fields = (d["fields"] as JsonArray ?? []).OfType<JsonObject>().Select(f => new RegisterField(
            (string)f["id"]!, (string?)f["title"] ?? (string)f["id"]!, (string?)f["type"] ?? "text", (bool?)f["required"] ?? false,
            (bool?)f["personal"] ?? false, (string?)f["mask"], (bool?)f["lookup"] ?? false, (string?)f["iso"], (string?)f["references"], (string?)f["pattern"])).ToList();
        var key = (string?)d["key"] ?? throw new InvalidOperationException("A register kind names its key");
        if (fields.All(f => f.Id != key)) throw new InvalidOperationException($"The key {key} is not a field of the register kind");
        foreach (var f in fields)
            if (f.Type is not ("text" or "code" or "date" or "boolean" or "list")) throw new InvalidOperationException($"The field {f.Id} has an unknown type {f.Type}");
            else if (f.Personal && f.Mask is null) throw new InvalidOperationException($"The field {f.Id} is personal data and declares no mask");
        return new RegisterKind((string)d["name"]!, (string?)d["title"] ?? (string)d["name"]!, (string?)d["description"] ?? "", key, (string?)d["titleField"] ?? key, fields, d);
    }
}

/// <summary>The register kinds an installation has: the runtime's own declarations, and any in Operations:Registers.</summary>
public sealed class RegisterCatalog
{
    readonly Dictionary<string, RegisterKind> kinds = new(StringComparer.Ordinal);
    public IReadOnlyCollection<RegisterKind> All => kinds.Values;

    public RegisterCatalog(IEnumerable<RegisterKind> ks) { foreach (var k in ks) kinds[k.Name] = k; }

    public RegisterKind? Find(string name) => kinds.GetValueOrDefault(name);

    /// <summary>The declarations shipped with the runtime (embedded *.register.json), and those in a folder, which may add kinds.</summary>
    public static RegisterCatalog Load(string? folder = null)
    {
        var docs = new List<JsonObject>();
        var asm = typeof(RegisterCatalog).Assembly;
        foreach (var r in asm.GetManifestResourceNames().Where(n => n.EndsWith(".register.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(r)!;
            docs.Add((JsonObject)JsonNode.Parse(s)!);
        }
        if (folder is { Length: > 0 } && Directory.Exists(folder))
            foreach (var f in Directory.EnumerateFiles(folder, "*.register.json").Order(StringComparer.Ordinal))
                docs.Add((JsonObject)JsonNode.Parse(File.ReadAllText(f))!);
        return new RegisterCatalog(docs.Select(RegisterKind.Parse));
    }
}

/// <summary>A record a maker submits: its fields (only those it changes, when the record exists), and from when.</summary>
public sealed record RegisterInput(JsonObject Fields, DateTimeOffset? ValidFrom = null);

public sealed class RegisterRefused(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>A version as the console and the APIs show it.</summary>
public sealed record RegisterVersionView(long Id, string Key, int VersionNo, DateTimeOffset ValidFrom, string ChangeId, string State,
    string MadeBy, DateTimeOffset MadeAt, string? DecidedBy, DateTimeOffset? DecidedAt, DateTimeOffset? EffectiveFrom, JsonObject Fields);

public sealed class RegisterService(StoreOptions store, RegisterCatalog catalog, TimeProvider? clock = null)
{
    public const string FactSubjectKind = "register";
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public RegisterCatalog Catalog => catalog;

    public RegisterKind Kind(string name) => catalog.Find(name) ?? throw new RegisterRefused(404, $"No register kind {name}");

    // ------------------------------------------------------------------ drafting

    /// <summary>
    /// A maker's change: each record merged onto its latest approved version (a field not given keeps its value), checked against
    /// the kind, and drafted as a new version. A record that changes nothing is left out; a change with nothing in it is not made
    /// (null). A record with a draft still waiting for consent is refused (409). A sync's reference makes the call idempotent.
    /// </summary>
    public async Task<RegisterChangeRow?> Draft(string tenant, string kindName, string maker, string source, IReadOnlyList<RegisterInput> records,
        string? remarks = null, string? reference = null, CancellationToken ct = default)
    {
        var kind = Kind(kindName);
        if (records.Count == 0) throw new RegisterRefused(400, "The change holds no record");
        await using var db = StoreDbContext.Open(store);
        if (reference is { Length: > 0 } && await db.RegisterChanges.AsNoTracking().FirstOrDefaultAsync(c => c.Tenant == tenant && c.Kind == kind.Name && c.Reference == reference, ct) is { } same)
            return same;
        var now = StoreTime.Of(clock.GetUtcNow());
        var change = new RegisterChangeRow { Id = $"RC-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..40], Tenant = tenant, Kind = kind.Name, Source = source, MadeBy = maker, MadeAt = now, Remarks = remarks, Reference = reference };
        var problems = new List<string>();
        var versions = new List<(RegisterVersionRow row, JsonObject data)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var decided = db.RegisterDecisions.AsNoTracking().Select(d => d.ChangeId);
        foreach (var (input, i) in records.Select((r, i) => (r, i + 1)))
        {
            var key = Text(input.Fields[kind.Key]);
            if (key is null) { problems.Add($"record {i} has no {kind.Key}"); continue; }
            if (!keys.Add(key)) { problems.Add($"record {i} repeats {kind.Key} {key}"); continue; }
            var mine = await db.RegisterVersions.AsNoTracking().Where(v => v.Tenant == tenant && v.Kind == kind.Name && v.Key == key).OrderBy(v => v.VersionNo).ToListAsync(ct);
            var ids = mine.Select(v => v.ChangeId).Distinct().ToList();
            var decisions = await db.RegisterDecisions.AsNoTracking().Where(d => ids.Contains(d.ChangeId)).ToDictionaryAsync(d => d.ChangeId, ct);
            if (mine.Any(v => !decisions.ContainsKey(v.ChangeId))) throw new RegisterRefused(409, $"{kind.Title} {key} has a change waiting for the bank approver's consent");
            var approved = mine.LastOrDefault(v => decisions.TryGetValue(v.ChangeId, out var d) && d.Decision == "approved");
            var data = approved is null ? new JsonObject() : (JsonObject)JsonNode.Parse(approved.DataJson)!;
            foreach (var (k, v) in input.Fields) data[k] = v?.DeepClone();
            problems.AddRange(Check(kind, data).Select(p => $"{kind.Key} {key}: {p}"));
            var canon = CanonicalJson.Of(data);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canon)));
            if (approved is not null && approved.Hash == hash && input.ValidFrom is null) continue;
            versions.Add((new RegisterVersionRow
            {
                ChangeId = change.Id, Tenant = tenant, Kind = kind.Name, Key = key, VersionNo = (mine.LastOrDefault()?.VersionNo ?? 0) + 1,
                ValidFrom = StoreTime.Of(input.ValidFrom ?? now), DataJson = canon, Hash = hash,
            }, data));
        }
        if (problems.Count > 0) throw new RegisterRefused(422, string.Join("; ", problems));
        if (versions.Count == 0) return null;
        change.Count = versions.Count;
        db.RegisterChanges.Add(change);
        foreach (var (row, _) in versions) db.RegisterVersions.Add(row);
        await db.SaveChangesAsync(ct);
        foreach (var (row, data) in versions)
            foreach (var f in kind.Lookups)
                foreach (var v in Values(data[f.Id]))
                    db.RegisterIndex.Add(new RegisterIndexRow { VersionId = row.Id, Tenant = tenant, Kind = kind.Name, Field = f.Id, Value = v });
        await db.SaveChangesAsync(ct);
        await new FactLog(db, clock).Append(change.Id, FactSubjectKind, new NewFact("register.drafted", kind.Name,
            new JsonObject { ["tenant"] = tenant, ["source"] = source, ["count"] = versions.Count, ["keys"] = new JsonArray(versions.Select(v => (JsonNode)v.row.Key).ToArray()) }, null, maker), ct);
        return change;
    }

    /// <summary>
    /// The bank approver's decision on a change (R-05-63): its maker may not decide it, and a change is decided once. The caller
    /// has already checked the approver's role in the tenant.
    /// </summary>
    public async Task<RegisterDecisionRow> Decide(string changeId, string approver, bool approve, string? remarks = null, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var change = await db.RegisterChanges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == changeId, ct) ?? throw new RegisterRefused(404, $"No register change {changeId}");
        if (string.Equals(change.MadeBy, approver, StringComparison.Ordinal)) throw new RegisterRefused(403, "The maker of a change may not consent to it: a different person with the bank approver's role decides it");
        if (await db.RegisterDecisions.AnyAsync(d => d.ChangeId == changeId, ct)) throw new RegisterRefused(409, $"{changeId} is already decided");
        var row = new RegisterDecisionRow { ChangeId = changeId, Decision = approve ? "approved" : "rejected", DecidedBy = approver, DecidedAt = StoreTime.Of(clock.GetUtcNow()), Remarks = remarks };
        db.RegisterDecisions.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new RegisterRefused(409, $"{changeId} is already decided"); }
        await new FactLog(db, clock).Append(changeId, FactSubjectKind, new NewFact(approve ? "register.approved" : "register.rejected", change.Kind,
            new JsonObject { ["tenant"] = change.Tenant, ["remarks"] = remarks }, null, approver), ct);
        return row;
    }

    public async Task<RegisterChangeRow?> Change(string id, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        return await db.RegisterChanges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <summary>The changes of the tenants given that wait for consent, oldest first.</summary>
    public async Task<List<JsonObject>> Pending(IReadOnlyCollection<string> tenants, bool masked = true, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var changes = await db.RegisterChanges.AsNoTracking().Where(c => tenants.Contains(c.Tenant) && !db.RegisterDecisions.Any(d => d.ChangeId == c.Id)).OrderBy(c => c.MadeAt).ToListAsync(ct);
        var ids = changes.Select(c => c.Id).ToList();
        var versions = await db.RegisterVersions.AsNoTracking().Where(v => ids.Contains(v.ChangeId)).OrderBy(v => v.Key).ToListAsync(ct);
        return changes.Select(c => ChangeJson(c, null, versions.Where(v => v.ChangeId == c.Id), masked)).ToList();
    }

    public async Task<JsonObject?> ChangeDetail(string id, bool masked = true, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var c = await db.RegisterChanges.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;
        var d = await db.RegisterDecisions.AsNoTracking().FirstOrDefaultAsync(x => x.ChangeId == id, ct);
        var versions = await db.RegisterVersions.AsNoTracking().Where(v => v.ChangeId == id).OrderBy(v => v.Key).ToListAsync(ct);
        return ChangeJson(c, d, versions, masked);
    }

    JsonObject ChangeJson(RegisterChangeRow c, RegisterDecisionRow? d, IEnumerable<RegisterVersionRow> versions, bool masked)
    {
        var kind = catalog.Find(c.Kind);
        return new JsonObject
        {
            ["id"] = c.Id, ["tenant"] = c.Tenant, ["kind"] = c.Kind, ["source"] = c.Source, ["madeBy"] = c.MadeBy, ["madeAt"] = c.MadeAt, ["remarks"] = c.Remarks,
            ["reference"] = c.Reference, ["count"] = c.Count, ["state"] = d?.Decision ?? "pending", ["decidedBy"] = d?.DecidedBy, ["decidedAt"] = d?.DecidedAt,
            ["records"] = new JsonArray(versions.Select(v => (JsonNode)new JsonObject
            {
                ["key"] = v.Key, ["versionNo"] = v.VersionNo, ["validFrom"] = v.ValidFrom,
                ["fields"] = kind is null ? new JsonObject() : Show(kind, (JsonObject)JsonNode.Parse(v.DataJson)!, masked),
            }).ToArray()),
        };
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Every version of a record, newest first, with its state: pending, approved or rejected, and from when it took effect.</summary>
    public async Task<List<RegisterVersionView>> History(string tenant, string kindName, string key, bool masked = true, CancellationToken ct = default)
    {
        var kind = Kind(kindName);
        await using var db = StoreDbContext.Open(store);
        var rows = await db.RegisterVersions.AsNoTracking().Where(v => v.Tenant == tenant && v.Kind == kind.Name && v.Key == key).OrderByDescending(v => v.VersionNo).ToListAsync(ct);
        return await Views(db, kind, rows, masked, ct);
    }

    async Task<List<RegisterVersionView>> Views(StoreDbContext db, RegisterKind kind, List<RegisterVersionRow> rows, bool masked, CancellationToken ct)
    {
        var ids = rows.Select(r => r.ChangeId).Distinct().ToList();
        var changes = await db.RegisterChanges.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var decisions = await db.RegisterDecisions.AsNoTracking().Where(d => ids.Contains(d.ChangeId)).ToDictionaryAsync(d => d.ChangeId, ct);
        return rows.Select(r =>
        {
            var c = changes[r.ChangeId];
            decisions.TryGetValue(r.ChangeId, out var d);
            DateTimeOffset? eff = d?.Decision == "approved" ? (r.ValidFrom > d.DecidedAt ? r.ValidFrom : d.DecidedAt) : null;
            return new RegisterVersionView(r.Id, r.Key, r.VersionNo, r.ValidFrom, r.ChangeId, d?.Decision ?? "pending", c.MadeBy, c.MadeAt, d?.DecidedBy, d?.DecidedAt, eff,
                Show(kind, (JsonObject)JsonNode.Parse(r.DataJson)!, masked));
        }).ToList();
    }

    /// <summary>
    /// The record in force at a moment: of its approved versions, the latest whose effect has begun, an effect beginning at the
    /// later of its validFrom and its consent. Unmasked: for the runtime's own reads (the party register port); never shown.
    /// </summary>
    public async Task<JsonObject?> InForce(string tenant, string kindName, string key, DateTimeOffset at, CancellationToken ct = default)
    {
        var kind = Kind(kindName);
        await using var db = StoreDbContext.Open(store);
        return (await InForceOf(db, tenant, kind, db.RegisterVersions.AsNoTracking().Where(v => v.Tenant == tenant && v.Kind == kind.Name && v.Key == key), at, ct)).Values.FirstOrDefault();
    }

    /// <summary>The records in force whose lookup field holds the value (a CIF, an establishment id, an account).</summary>
    public async Task<List<JsonObject>> FindBy(string tenant, string kindName, string field, string value, DateTimeOffset at, CancellationToken ct = default)
    {
        var kind = Kind(kindName);
        if (kind.Field(field) is not { Lookup: true }) throw new RegisterRefused(400, $"{kind.Title} is not looked up by {field}");
        await using var db = StoreDbContext.Open(store);
        var keys = db.RegisterIndex.AsNoTracking().Where(x => x.Tenant == tenant && x.Kind == kind.Name && x.Field == field && x.Value == value)
            .Join(db.RegisterVersions, x => x.VersionId, v => v.Id, (x, v) => v.Key).Distinct();
        var found = await InForceOf(db, tenant, kind, db.RegisterVersions.AsNoTracking().Where(v => v.Tenant == tenant && v.Kind == kind.Name && keys.Contains(v.Key)), at, ct);
        // The index holds every version's values: keep a record only if the version in force still holds the value.
        return found.Values.Where(r => Values(r[field]).Contains(value, StringComparer.Ordinal)).ToList();
    }

    /// <summary>The records in force, by text over the key, the title field and the lookup fields; a page of them, and how many in all.</summary>
    public async Task<(int total, List<JsonObject> rows)> Search(string tenant, string kindName, string? q, DateTimeOffset at, int page = 1, int size = 25, CancellationToken ct = default)
    {
        var kind = Kind(kindName);
        await using var db = StoreDbContext.Open(store);
        var all = (await InForceOf(db, tenant, kind, db.RegisterVersions.AsNoTracking().Where(v => v.Tenant == tenant && v.Kind == kind.Name), at, ct)).Values.ToList();
        if (q is { Length: > 0 })
            all = all.Where(r => new[] { kind.Key, kind.TitleField }.Concat(kind.Lookups.Select(f => f.Id)).Distinct()
                .SelectMany(f => Values(r[f])).Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        var ordered = all.OrderBy(r => Text(r[kind.Key]), StringComparer.Ordinal).ToList();
        size = Math.Clamp(size, 1, 500);
        return (ordered.Count, ordered.Skip((Math.Max(1, page) - 1) * size).Take(size).ToList());
    }

    async Task<Dictionary<string, JsonObject>> InForceOf(StoreDbContext db, string tenant, RegisterKind kind, IQueryable<RegisterVersionRow> versions, DateTimeOffset at, CancellationToken ct)
    {
        var t = StoreTime.Of(at);
        var rows = await versions.Join(db.RegisterDecisions.Where(d => d.Decision == "approved"), v => v.ChangeId, d => d.ChangeId, (v, d) => new { v, d.DecidedAt })
            .Where(x => x.v.ValidFrom <= t && x.DecidedAt <= t).ToListAsync(ct);
        return rows.GroupBy(x => x.v.Key).ToDictionary(g => g.Key, g => (JsonObject)JsonNode.Parse(g.OrderBy(x => x.v.VersionNo).Last().v.DataJson)!, StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ fields

    /// <summary>A record as it is shown: personal fields and the fields that declare a mask, masked as the operations views mask.</summary>
    public static JsonObject Show(RegisterKind kind, JsonObject data, bool masked = true)
    {
        var o = new JsonObject();
        foreach (var f in kind.Fields)
        {
            var v = data[f.Id];
            if (v is null) continue;
            if (!masked || f.Mask is null or "none") { o[f.Id] = v.DeepClone(); continue; }
            o[f.Id] = v is JsonArray a ? new JsonArray(a.Select(x => (JsonNode?)JsonValue.Create(Mask.Apply(f.Mask, Text(x)))).ToArray()) : JsonValue.Create(Mask.Apply(f.Mask, Text(v)));
        }
        return o;
    }

    /// <summary>What is wrong with a record against its kind: a missing required field, an unknown field, a value of the wrong type.</summary>
    public static List<string> Check(RegisterKind kind, JsonObject data)
    {
        var out_ = new List<string>();
        foreach (var (k, _) in data) if (kind.Field(k) is null) out_.Add($"{k} is not a field of {kind.Title}");
        foreach (var f in kind.Fields)
        {
            var v = data[f.Id];
            if (v is null || (v is JsonValue jv && jv.TryGetValue<string>(out var s0) && s0.Length == 0))
            {
                if (f.Required) out_.Add($"{f.Id} is required");
                continue;
            }
            var ok = f.Type switch
            {
                "boolean" => v is JsonValue b && b.TryGetValue<bool>(out _),
                "list" => v is JsonArray arr && arr.All(x => x is JsonValue xv && xv.TryGetValue<string>(out _)),
                "date" => v is JsonValue d && d.TryGetValue<string>(out var ds) && DateOnly.TryParseExact(ds, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                _ => v is JsonValue t && t.TryGetValue<string>(out _),
            };
            if (!ok) out_.Add($"{f.Id} is not a {f.Type}{(f.Type == "date" ? " (yyyy-MM-dd)" : "")}");
            else if (f.Pattern is { } p && v is JsonValue pv && pv.TryGetValue<string>(out var ps) && !Regex.IsMatch(ps, p)) out_.Add($"{f.Id} does not match {p}");
        }
        return out_;
    }

    static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? (s.Length == 0 ? null : s) : n?.ToJsonString();

    static IEnumerable<string> Values(JsonNode? n) => n switch
    {
        JsonArray a => a.Select(Text).Where(x => x is not null).Select(x => x!),
        null => [],
        _ => Text(n) is { } s ? [s] : [],
    };
}
