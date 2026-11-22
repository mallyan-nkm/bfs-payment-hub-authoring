using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace TransactHub.Store;

/// <summary>
/// The release registry (plan W4): a package is stored once and never changed; which release new
/// payments take is a list of activation rows, also never changed. Rollback is a new activation.
/// </summary>
public sealed class ReleaseRegistry(StoreDbContext db, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public static string HashOf(byte[] package) => Convert.ToHexStringLower(SHA256.HashData(package));

    /// <summary>
    /// Stores a release. The same release with the same content is accepted again and changes nothing;
    /// the same release with other content is refused.
    /// </summary>
    /// <param name="contentHash">The release's content hash, when it is not the hash of the stored bytes (a signed package
    /// is hashed without its signature, so a package signed again is the same release).</param>
    public async Task<StoreResult> Store(string scope, string release, byte[] package, JsonObject manifest, string actor, CancellationToken ct = default, string? contentHash = null)
    {
        var hash = contentHash ?? HashOf(package);
        var existing = await db.Releases.AsNoTracking().FirstOrDefaultAsync(r => r.Scope == scope && r.Release == release, ct);
        if (existing is not null)
        {
            if (existing.ContentHash == hash) return new StoreResult(existing.Id, false);
            throw new ReleaseConflict($"{scope}@{release} is already stored with other content ({existing.ContentHash[..12]}…, this is {hash[..12]}…). Give the change a new release.");
        }
        var id = $"{scope}@{release}";
        db.Releases.Add(new ReleaseRow
        {
            Id = id, Scope = scope, Release = release, ContentHash = hash, ManifestJson = CanonicalJson.Of(manifest),
            Package = package, StoredBy = actor, StoredAt = StoreTime.Of(clock.GetUtcNow()),
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Another writer stored it first: the same rule applies to what it stored.
            db.ChangeTracker.Clear();
            return await Store(scope, release, package, manifest, actor, ct, contentHash);
        }
        return new StoreResult(id, true);
    }

    public async Task Activate(string environment, string scope, string release, DateTimeOffset effectiveFrom, string actor, CancellationToken ct = default)
    {
        if (!await db.Releases.AnyAsync(r => r.Scope == scope && r.Release == release, ct))
            throw new InvalidOperationException($"{scope}@{release} is not stored; store it before activating it.");
        db.Activations.Add(new ActivationRow
        {
            Environment = environment, Scope = scope, Release = release, EffectiveFrom = StoreTime.Of(effectiveFrom),
            Actor = actor, RecordedAt = StoreTime.Of(clock.GetUtcNow()),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The release new payments take at a moment: the latest activation in effect then (the later recorded wins a tie).</summary>
    public async Task<string?> ActiveAt(string environment, string scope, DateTimeOffset at, CancellationToken ct = default)
    {
        var t = StoreTime.Of(at);
        return await db.Activations.AsNoTracking()
            .Where(a => a.Environment == environment && a.Scope == scope && a.EffectiveFrom <= t)
            .OrderByDescending(a => a.EffectiveFrom).ThenByDescending(a => a.Id)
            .Select(a => a.Release).FirstOrDefaultAsync(ct);
    }

    public Task<ReleaseRow?> Get(string scope, string release, CancellationToken ct = default) =>
        db.Releases.AsNoTracking().FirstOrDefaultAsync(r => r.Scope == scope && r.Release == release, ct);
}

public sealed record StoreResult(string Id, bool Stored);
public sealed class ReleaseConflict(string message) : InvalidOperationException(message);

/// <summary>
/// Rules, lists and configuration values, effective-dated and published as new versions (plan W4).
/// The version in force at a moment is the newest version published by then whose dates cover it:
/// dates first, then the version (the source's RuleRepository chose the version first, issue N1).
/// </summary>
public sealed class VersionedValues(StoreDbContext db, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<int> Publish(string kind, string tenant, string name, JsonNode body, DateTimeOffset effectiveFrom, DateTimeOffset? effectiveTo, string actor, CancellationToken ct = default)
    {
        if (effectiveTo is { } to && to <= effectiveFrom) throw new ArgumentException("A version must end after it starts.");
        for (var attempt = 0; ; attempt++)
        {
            var last = await db.VersionedValues.AsNoTracking().Where(v => v.Kind == kind && v.Tenant == tenant && v.Name == name)
                .MaxAsync(v => (int?)v.VersionNo, ct) ?? 0;
            var row = new VersionedValueRow
            {
                Kind = kind, Tenant = tenant, Name = name, VersionNo = last + 1,
                EffectiveFrom = StoreTime.Of(effectiveFrom), EffectiveTo = effectiveTo is { } e ? StoreTime.Of(e) : null,
                BodyJson = CanonicalJson.Of(body), PublishedAt = StoreTime.Of(clock.GetUtcNow()), PublishedBy = actor,
            };
            db.VersionedValues.Add(row);
            try { await db.SaveChangesAsync(ct); return row.VersionNo; }
            catch (DbUpdateException) when (attempt < 5) { db.Entry(row).State = EntityState.Detached; }
        }
    }

    /// <summary>
    /// The version in force as of a moment, knowing only what was published by then. A payment that
    /// evaluates as of its acceptance gets the same answer every time it asks, even after later publishes.
    /// </summary>
    public async Task<VersionedValueRow?> InForce(string kind, string tenant, string name, DateTimeOffset asOf, CancellationToken ct = default)
    {
        var t = StoreTime.Of(asOf);
        return await db.VersionedValues.AsNoTracking()
            .Where(v => v.Kind == kind && v.Tenant == tenant && v.Name == name
                        && v.PublishedAt <= t && v.EffectiveFrom <= t && (v.EffectiveTo == null || v.EffectiveTo > t))
            .OrderByDescending(v => v.VersionNo)
            .FirstOrDefaultAsync(ct);
    }
}
