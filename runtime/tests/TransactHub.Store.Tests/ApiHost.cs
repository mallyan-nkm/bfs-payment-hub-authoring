using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TransactHub.Security;

namespace TransactHub.Store.Tests;

/// <summary>
/// A signing key made for the test (never written beyond the test's folder), and the trusted-signers file the runtime
/// reads, which a test may rewrite to rotate or revoke a key while the API runs.
/// </summary>
public sealed class TestSigners : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("th-trust-").FullName;
    public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string KeyId => ReleaseSignature.KeyIdOf(Key);
    public string TrustFile => Path.Combine(dir, "trusted-signers.json");

    public TestSigners() => Trust([Key]);

    /// <summary>Writes the trusted-signers file: these keys trusted, those revoked.</summary>
    public void Trust(IEnumerable<ECDsa> keys, IEnumerable<ECDsa>? revoked = null) =>
        File.WriteAllText(TrustFile, new JsonObject
        {
            ["kind"] = "trusted-signers",
            ["keys"] = new JsonArray(keys.Select(k => (JsonNode)new JsonObject { ["keyId"] = ReleaseSignature.KeyIdOf(k), ["publicKey"] = k.ExportSubjectPublicKeyInfoPem() }).ToArray()),
            ["revoked"] = new JsonArray((revoked ?? []).Select(k => (JsonNode)new JsonObject { ["keyId"] = ReleaseSignature.KeyIdOf(k), ["revokedAt"] = "2026-11-22T00:00:00Z", ["reason"] = "test" }).ToArray()),
        }.ToJsonString());

    /// <summary>The package's files with the studio's signature beside them, as the studio writes it at release.</summary>
    public static Dictionary<string, byte[]> Sign(Dictionary<string, string> files, ECDsa key, string releaseId, string? tenant = null, string? changeSet = "CS-TEST-001")
    {
        var bytes = files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value), StringComparer.Ordinal);
        var parts = releaseId.Split('@');
        var wf = parts[0].Split('/');
        var (st, sig) = ReleaseSignature.Sign(bytes, new ReleaseClaims(releaseId, wf[0], parts[1], wf[1], tenant, changeSet), key, DateTimeOffset.UtcNow);
        bytes[ReleaseSignature.StatementFile] = st;
        bytes[ReleaseSignature.SignatureFile] = sig;
        return bytes;
    }

    public static byte[] Zip(IReadOnlyDictionary<string, byte[]> files, bool reverse = false)
    {
        var ms = new MemoryStream();
        using (var a = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (k, v) in reverse ? files.OrderByDescending(f => f.Key) : files.OrderBy(f => f.Key))
            {
                var e = a.CreateEntry(k);
                e.LastWriteTime = reverse ? DateTimeOffset.Now : new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var s = e.Open();
                s.Write(v);
            }
        return ms.ToArray();
    }

    public void Dispose()
    {
        Key.Dispose();
        Directory.Delete(dir, recursive: true);
    }
}

/// <summary>
/// The bank's identity provider, as a test makes it: an RSA key, its JWKS, and the tokens it signs. A machine caller's
/// token is what a client-credentials grant gives (sub and azp the client, app roles in <c>roles</c>); a person's carries
/// groups. Nothing is kept beyond the test.
/// </summary>
public sealed class TestIdp : IDisposable
{
    public const string Issuer = "https://idp.bank.example.test/realms/runtime";
    public const string Audience = "transacthub-runtime";
    public readonly RSA Rsa = RSA.Create(2048);

    public string Jwks
    {
        get
        {
            var p = Rsa.ExportParameters(false);
            return new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["kty"] = "RSA", ["kid"] = "test-1", ["use"] = "sig", ["alg"] = "RS256", ["n"] = Base64UrlEncoder.Encode(p.Modulus), ["e"] = Base64UrlEncoder.Encode(p.Exponent) }) }.ToJsonString();
        }
    }

    public string Token(string sub, IDictionary<string, object>? claims = null, string issuer = Issuer, string audience = Audience, TimeSpan? life = null, RSA? key = null)
    {
        var all = new Dictionary<string, object> { ["sub"] = sub };
        foreach (var (k, v) in claims ?? new Dictionary<string, object>()) all[k] = v;
        var now = DateTime.UtcNow;
        var exp = now.Add(life ?? TimeSpan.FromMinutes(10));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Claims = all, IssuedAt = exp.AddMinutes(-20), NotBefore = exp.AddMinutes(-20), Expires = exp,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key ?? Rsa) { KeyId = "test-1" }, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>A client-credentials token of a machine caller, with the app role the bank assigned it.</summary>
    public string Client(string client, string appRole) => Token(client, new Dictionary<string, object> { ["azp"] = client, ["roles"] = new[] { appRole } });

    public void Dispose() => Rsa.Dispose();
}

/// <summary>
/// The release API on a fresh database, trusting the test's identity provider (its JWKS given inline, as an installation
/// without discovery would) and a role map from its app roles and groups to the runtime's roles, and the test's trusted
/// signers. <see cref="Client"/> gives a client with a machine caller's token.
/// </summary>
public sealed class ApiHost(StoreOptions o, TestSigners signers, Action<IWebHostBuilder>? more = null) : WebApplicationFactory<Program>
{
    /// <summary>The machine callers: client id and the app role the bank gave it.</summary>
    public static readonly Dictionary<string, string> Clients = new()
    {
        ["pipeline"] = "TransactHub.Pipeline",
        ["importer"] = "TransactHub.Importer",
        ["reader"] = "TransactHub.Reader",
        ["bank-worker"] = "TransactHub.Worker",
    };

    /// <summary>The bank's role map: app roles and a group to the runtime's roles; a tenant claim in the token gives tenants.</summary>
    public const string RoleMap = """
        { "kind": "runtime-role-map", "tenantsClaim": "tenants", "grants": [
          { "claim": "roles", "value": "TransactHub.Pipeline", "roles": ["release-importer", "release-activator", "release-reader", "values-reader", "payment-intake"], "tenants": ["*"] },
          { "claim": "roles", "value": "TransactHub.Importer", "roles": ["release-importer"] },
          { "claim": "roles", "value": "TransactHub.Reader", "roles": ["release-reader"] },
          { "claim": "roles", "value": "TransactHub.Worker", "roles": ["values-reader"], "tenants": ["example-bank"] },
          { "claim": "groups", "value": "grp-runtime-operations", "roles": ["release-importer", "release-activator", "release-reader"] },
          { "claim": "groups", "value": "grp-bank-analysts", "roles": ["release-reader", "values-reader"] } ] }
        """;

    public readonly TestIdp Idp = new();

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseSetting("Store:Provider", o.Provider.ToString());
        b.UseSetting("Store:Connection", o.ConnectionString);
        b.UseSetting("TH_TRUSTED_SIGNERS", $"file:{signers.TrustFile}");
        b.UseSetting("Auth:Oidc:Issuer", TestIdp.Issuer);
        b.UseSetting("Auth:Oidc:Audience", TestIdp.Audience);
        b.UseSetting("Auth:Oidc:SigningKeys", Idp.Jwks);
        b.UseSetting("Auth:RoleMap", RoleMap);
        more?.Invoke(b);
    }

    public HttpClient Client(string? client = "pipeline") => client is null ? CreateClient() : Bearer(Idp.Client(client, Clients[client]));

    public HttpClient Bearer(string token)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Idp.Dispose();
        base.Dispose(disposing);
    }
}
