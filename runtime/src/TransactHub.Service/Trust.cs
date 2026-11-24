using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TransactHub.Security;

namespace TransactHub.Service;

/// <summary>
/// The trusted signers as the service console's trust page shows them (R-09-249, Issue I-292): each key with what it may sign (its
/// kinds, or every kind where it names none, R-09-184) and its scope (the feed it is scoped to), whether it is retired or revoked;
/// and the rates feed's own key, the public half of the feed's signing key (<c>Service:FxRates:SigningKeyRef</c>), with whether the
/// list trusts it for the feed and the entry <c>rotate-trusted-key</c> adds for it: kinds [configuration], scope { feed: fx-rates }.
/// Public keys only: the feed's private key is read to derive its public half and never shown or kept.
/// </summary>
public sealed class TrustSettings(string trustSetting, ISecretResolver secrets, string? feedKeyRef, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public string Setting => trustSetting;
    public bool FeedConfigured => feedKeyRef is { Length: > 0 };

    /// <summary>The public half of the rates feed's signing key, as PEM, with its key id; null where the host runs no feed.</summary>
    public async Task<(string KeyId, string PublicKey)?> FeedKey(CancellationToken ct = default)
    {
        if (feedKeyRef is not { Length: > 0 } r) return null;
        using var key = ReleaseSignature.PrivateKeyFromPem(await secrets.Resolve(r, ct));
        using var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);
        return (ReleaseSignature.KeyIdOf(pub), pub.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>The entry the trusted signers take for the rates feed's key (R-09-184): it signs configuration releases of the feed only.</summary>
    public static JsonObject FeedEntry(string keyId, string publicKey) => new()
    {
        ["keyId"] = keyId, ["publicKey"] = publicKey,
        ["kinds"] = new JsonArray(ReleaseSignature.ConfigurationRelease), ["scope"] = new JsonObject { ["feed"] = FxRateFeed.Domain },
    };

    public async Task<JsonObject> Describe(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var list = await TrustedSigners.Load(trustSetting, secrets, ct);
        var keys = new JsonArray();
        foreach (var k in list.Keys.Values.OrderBy(k => k.RetiredAt is null ? 0 : 1).ThenBy(k => k.KeyId, StringComparer.Ordinal))
        {
            var revoked = list.Revoked.TryGetValue(k.KeyId, out var rv) ? rv : null;
            var state = revoked is not null && revoked.RevokedAt <= now ? "revoked" : k.RetiredAt is { } at && at <= now ? "retired" : k.RetiredAt is not null ? "retiring" : "trusted";
            keys.Add(new JsonObject
            {
                ["keyId"] = k.KeyId, ["state"] = state, ["retiredAt"] = k.RetiredAt?.ToString("O"), ["revokedAt"] = revoked?.RevokedAt.ToString("O"),
                // No kinds: the key signs every kind (R-09-184).
                ["kinds"] = new JsonArray((k.Kinds ?? ReleaseSignature.Kinds).Select(x => (JsonNode?)x).ToArray()), ["everyKind"] = k.Kinds is null,
                ["scope"] = k.Feed is null ? null : new JsonObject { ["feed"] = k.Feed },
            });
        }
        var revokedOnly = list.Revoked.Values.Where(r => !list.Keys.ContainsKey(r.KeyId))
            .Select(r => (JsonNode?)new JsonObject { ["keyId"] = r.KeyId, ["revokedAt"] = r.RevokedAt.ToString("O"), ["reason"] = r.Reason }).ToArray();
        JsonObject? feed = null;
        if (await FeedKey(ct) is { } fk)
        {
            var have = list.Keys.GetValueOrDefault(fk.KeyId);
            var ok = have is not null && have.RetiredAt is null && have.Kinds is [ReleaseSignature.ConfigurationRelease] && have.Feed == FxRateFeed.Domain;
            // The feed key this one would follow: a key scoped to the feed that is not retired, to retire when the new one is trusted.
            var previous = list.Keys.Values.FirstOrDefault(k => k.KeyId != fk.KeyId && k.Feed == FxRateFeed.Domain && k.RetiredAt is null)?.KeyId;
            feed = new JsonObject
            {
                ["feed"] = FxRateFeed.Domain, ["keyId"] = fk.KeyId, ["trusted"] = ok,
                ["state"] = ok ? "trusted" : have is null ? "not-trusted" : "trusted-otherwise",
                ["previous"] = previous, ["entry"] = FeedEntry(fk.KeyId, fk.PublicKey),
            };
        }
        return new JsonObject
        {
            ["at"] = now.ToString("O"), ["kinds"] = new JsonArray(ReleaseSignature.Kinds.Select(x => (JsonNode?)x).ToArray()),
            ["keys"] = keys, ["revoked"] = new JsonArray(revokedOnly), ["feed"] = feed,
        };
    }
}
