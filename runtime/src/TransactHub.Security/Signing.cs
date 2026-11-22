using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TransactHub.Security;

/// <summary>
/// The signature of a released package (spec R-09-23 to R-09-25). Beside the package's files are two more:
/// <list type="bullet">
/// <item><c>release.json</c>, the statement: canonical JSON (RFC 8785) naming its kind, the release id, workflow, release,
/// target, tenant, change set, signing key id, signing time and the SHA-256 of every other file of the package;</item>
/// <item><c>release.sig</c>, an ECDSA P-256 signature with SHA-256 over the statement's bytes, DER encoded, in Base64.</item>
/// </list>
/// That is the shape of a detached blob signature, so a person can check one with standard tools:
/// <c>openssl dgst -sha256 -verify signer.pem -signature &lt;(base64 -d release.sig) release.json</c>, or
/// <c>cosign verify-blob --key signer.pem --signature release.sig release.json</c>.
/// </summary>
public static class ReleaseSignature
{
    public const string StatementFile = "release.json";
    public const string SignatureFile = "release.sig";
    public const string Format = "th-release-statement/1";
    /// <summary>The kinds of signed package: a workflow package (built), and the per-bank distribution and configuration
    /// release of docs/runtime/PACKAGING-AND-DEPLOYMENT.md, which carry their bill of materials and parameter contract as
    /// further fields of the same statement.</summary>
    public const string WorkflowPackage = "workflow-package";
    public const string Distribution = "distribution";
    public const string ConfigurationRelease = "configuration";
    public static readonly string[] Kinds = [WorkflowPackage, Distribution, ConfigurationRelease];

    public static bool IsSignatureFile(string path) => path is StatementFile or SignatureFile;

    /// <summary>A key's id: the first 32 hex digits of the SHA-256 of its public key (SubjectPublicKeyInfo, DER).</summary>
    public static string KeyIdOf(ECDsa key) => Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..32];

    public static string Sha256Hex(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>A P-256 private key from PKCS#8 or SEC 1 PEM, as a secret holds it.</summary>
    public static ECDsa PrivateKeyFromPem(SecretValue pem)
    {
        var k = ECDsa.Create();
        try { k.ImportFromPem(pem.Reveal()); }
        catch (Exception e) when (e is ArgumentException or CryptographicException) { k.Dispose(); throw new SignatureRejected("The signing key is not a PEM EC private key"); }
        if (k.KeySize != 256) { k.Dispose(); throw new SignatureRejected("The signing key is not on P-256"); }
        return k;
    }

    /// <summary>Signs a package's files: the statement and its signature, to put beside them.</summary>
    public static (byte[] Statement, byte[] Signature) Sign(IReadOnlyDictionary<string, byte[]> files, ReleaseClaims claims, ECDsa key, DateTimeOffset signedAt)
    {
        var statement = new JsonObject
        {
            ["format"] = Format,
            ["kind"] = claims.Kind,
            ["releaseId"] = claims.ReleaseId,
            ["workflow"] = claims.Workflow,
            ["release"] = claims.Release,
            ["target"] = claims.Target,
            ["tenant"] = claims.Tenant,
            ["changeSet"] = claims.ChangeSet,
            ["keyId"] = KeyIdOf(key),
            ["signedAt"] = signedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["files"] = new JsonArray(files.Where(f => !IsSignatureFile(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => (JsonNode?)new JsonObject { ["path"] = f.Key, ["sha256"] = Sha256Hex(f.Value) }).ToArray()),
        };
        // A kind's own fields (a distribution's bill of materials, its parameter contract) are signed with the rest.
        foreach (var (k, v) in claims.Extra ?? [])
            if (!statement.ContainsKey(k)) statement[k] = v?.DeepClone();
        var bytes = Encoding.UTF8.GetBytes(Canonical(statement));
        var sig = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return (bytes, Encoding.ASCII.GetBytes(Convert.ToBase64String(sig) + "\n"));
    }

    /// <summary>
    /// Verifies a package against the keys the installation trusts. It refuses a package with no signature, a key that is
    /// not trusted or is revoked, a signature that does not verify, a key used after it was retired, a statement for another
    /// release or tenant, and any file that is missing, extra or does not match its hash.
    /// </summary>
    public static Verification Verify(IReadOnlyDictionary<string, byte[]> files, ReleaseExpectation expected, TrustedSigners trust)
    {
        if (!files.TryGetValue(StatementFile, out var bytes) || !files.TryGetValue(SignatureFile, out var sigText))
            throw new SignatureRejected($"The package is not signed: it has no {StatementFile} and {SignatureFile}");
        JsonObject st;
        try { st = JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new SignatureRejected($"{StatementFile} is not a JSON object"); }
        if ((string?)st["format"] != Format) throw new SignatureRejected($"{StatementFile} is not a {Format} statement");
        if ((string?)st["kind"] != expected.Kind) throw new SignatureRejected($"The signature is for a {(string?)st["kind"] ?? "package of no kind"}, not a {expected.Kind}");
        var keyId = (string?)st["keyId"] ?? throw new SignatureRejected($"{StatementFile} names no key");
        if (trust.Revoked.TryGetValue(keyId, out var revoked)) throw new SignatureRejected($"The package is signed by key {keyId}, which was revoked on {revoked.RevokedAt:O}");
        if (!trust.Keys.TryGetValue(keyId, out var signer)) throw new SignatureRejected($"The package is signed by key {keyId}, which this installation does not trust");
        byte[] sig;
        try { sig = Convert.FromBase64String(Encoding.ASCII.GetString(sigText).Trim()); }
        catch (FormatException) { throw new SignatureRejected($"{SignatureFile} is not Base64"); }
        using (var pub = ECDsa.Create())
        {
            pub.ImportFromPem(signer.PublicKeyPem);
            if (!pub.VerifyData(bytes, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                throw new SignatureRejected($"The signature does not verify with key {keyId}: {StatementFile} or {SignatureFile} was changed");
        }
        if (!DateTimeOffset.TryParse((string?)st["signedAt"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var signedAt))
            throw new SignatureRejected($"{StatementFile} has no signing time");
        if (signer.RetiredAt is { } retired && signedAt > retired)
            throw new SignatureRejected($"The package was signed on {signedAt:O} by key {keyId}, which was retired on {retired:O}");
        if ((string?)st["releaseId"] != expected.ReleaseId) throw new SignatureRejected($"The signature is for {(string?)st["releaseId"]}, not {expected.ReleaseId}");
        if ((string?)st["tenant"] != expected.Tenant) throw new SignatureRejected($"The signature is for tenant {(string?)st["tenant"] ?? "(shared)"}, not {expected.Tenant ?? "(shared)"}");
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in st["files"] as JsonArray ?? throw new SignatureRejected($"{StatementFile} lists no files"))
            listed[(string?)f?["path"] ?? throw new SignatureRejected($"A file of {StatementFile} has no path")] = (string?)f?["sha256"] ?? "";
        foreach (var (path, content) in files)
        {
            if (IsSignatureFile(path)) continue;
            if (!listed.TryGetValue(path, out var h)) throw new SignatureRejected($"{path} is not in the signed statement");
            if (h != Sha256Hex(content)) throw new SignatureRejected($"{path} does not match its hash in the signed statement");
        }
        var missing = listed.Keys.Where(k => !files.ContainsKey(k)).ToList();
        if (missing.Count > 0) throw new SignatureRejected($"Signed but not in the package: {string.Join(", ", missing.Take(5))}");
        return new Verification(keyId, Sha256Hex(bytes), (string?)st["changeSet"], signedAt, listed.Count, expected.Kind);
    }

    /// <summary>RFC 8785 for the statement's shape (strings, arrays, objects, null): keys in ordinal order, no whitespace.</summary>
    public static string Canonical(JsonNode? n)
    {
        var sb = new StringBuilder();
        void W(JsonNode? x)
        {
            switch (x)
            {
                case null: sb.Append("null"); break;
                case JsonObject o:
                    sb.Append('{');
                    var first = true;
                    foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(JsonSerializer.Serialize(k)).Append(':');
                        W(v);
                    }
                    sb.Append('}');
                    break;
                case JsonArray a:
                    sb.Append('[');
                    for (var i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); W(a[i]); }
                    sb.Append(']');
                    break;
                default: sb.Append(x.ToJsonString()); break;
            }
        }
        W(n);
        return sb.ToString();
    }
}

/// <summary>What a signature says the package is.</summary>
/// <param name="Extra">Fields of the statement a kind adds (a distribution's <c>billOfMaterials</c>, <c>parameterContract</c>).</param>
public sealed record ReleaseClaims(string ReleaseId, string Workflow, string Release, string Target, string? Tenant, string? ChangeSet, string Kind = ReleaseSignature.WorkflowPackage, JsonObject? Extra = null);

/// <summary>What the runtime expects of a package's signature: the release it stores it as, and the manifest's tenant.</summary>
public sealed record ReleaseExpectation(string ReleaseId, string? Tenant, string Kind = ReleaseSignature.WorkflowPackage);

/// <summary>The outcome of a verification, recorded as a fact on the release. It holds no secret.</summary>
public sealed record Verification(string KeyId, string StatementSha256, string? ChangeSet, DateTimeOffset SignedAt, int Files, string Kind = ReleaseSignature.WorkflowPackage);

public sealed class SignatureRejected(string message) : Exception(message);

/// <summary>A public key the installation trusts, with the moment it stopped signing new releases, if it has.</summary>
public sealed record TrustedSigner(string KeyId, string PublicKeyPem, DateTimeOffset? RetiredAt);

public sealed record RevokedSigner(string KeyId, DateTimeOffset RevokedAt, string? Reason);

/// <summary>
/// The signers an installation trusts (<c>TH_TRUSTED_SIGNERS</c>): several keys by id, so a new key is added before the
/// old one retires; a retired key still verifies what it signed before it retired; a revoked key verifies nothing.
/// <code>{ "kind": "trusted-signers", "keys": [{ "keyId", "publicKey", "retiredAt"? }], "revoked": [{ "keyId", "revokedAt", "reason"? }] }</code>
/// Each key id is checked against its key, so a list cannot name one key with another's id.
/// </summary>
public sealed class TrustedSigners
{
    public required IReadOnlyDictionary<string, TrustedSigner> Keys { get; init; }
    public required IReadOnlyDictionary<string, RevokedSigner> Revoked { get; init; }

    public static TrustedSigners Parse(string json)
    {
        JsonObject o;
        try { o = JsonNode.Parse(json) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new SignatureRejected("The trusted signers are not a JSON object"); }
        if ((string?)o["kind"] != "trusted-signers") throw new SignatureRejected("The trusted signers document is not of kind trusted-signers");
        var keys = new Dictionary<string, TrustedSigner>(StringComparer.Ordinal);
        foreach (var k in o["keys"] as JsonArray ?? [])
        {
            var id = (string?)k?["keyId"] ?? throw new SignatureRejected("A trusted key has no keyId");
            var pem = (string?)k?["publicKey"] ?? throw new SignatureRejected($"Trusted key {id} has no publicKey");
            using var e = ECDsa.Create();
            try { e.ImportFromPem(pem); }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException) { throw new SignatureRejected($"Trusted key {id} is not a PEM EC public key"); }
            if (e.KeySize != 256) throw new SignatureRejected($"Trusted key {id} is not on P-256");
            if (ReleaseSignature.KeyIdOf(e) != id) throw new SignatureRejected($"Trusted key {id} is not the id of its public key ({ReleaseSignature.KeyIdOf(e)})");
            keys[id] = new TrustedSigner(id, pem, (string?)k?["retiredAt"] is { } r ? DateTimeOffset.Parse(r, CultureInfo.InvariantCulture) : null);
        }
        var revoked = new Dictionary<string, RevokedSigner>(StringComparer.Ordinal);
        foreach (var r in o["revoked"] as JsonArray ?? [])
        {
            var id = (string?)r?["keyId"] ?? throw new SignatureRejected("A revoked key has no keyId");
            revoked[id] = new RevokedSigner(id, (string?)r?["revokedAt"] is { } at ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture) : DateTimeOffset.MinValue, (string?)r?["reason"]);
        }
        return new TrustedSigners { Keys = keys, Revoked = revoked };
    }

    /// <summary>
    /// The trusted signers from their setting: the JSON itself, or a reference to where it is (file:/path, env:NAME,
    /// vault:…). Read again at each use, so adding, retiring or revoking a key needs no restart.
    /// </summary>
    public static async Task<TrustedSigners> Load(string setting, ISecretResolver resolver, CancellationToken ct = default) =>
        Parse(setting.TrimStart().StartsWith('{') ? setting : (await resolver.Resolve(setting, ct)).Reveal());
}
