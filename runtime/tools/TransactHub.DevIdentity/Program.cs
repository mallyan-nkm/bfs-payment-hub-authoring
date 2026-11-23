using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Security;

// A development identity provider for one bank's runtime on a laptop (deploy/laptop, spec R-09-77). It plays the bank's OIDC
// issuer, which the runtime trusts and keeps no identities of (R-09-27): discovery, a JWKS, the authorisation code flow with
// PKCE for the runtime's web app (a page of synthetic users to choose from), client credentials for the machine callers, and,
// for the laptop's own tests only, the password grant for a synthetic user. Its RSA key is made on first start and kept in its
// volume; its public JWKS is written where the runtime's hosts read it (Auth:Oidc:SigningKeys=file:...). It holds no real user,
// no secret value (client secrets are references, resolved from its environment) and refuses to start in Production.
//
//   TransactHub.DevIdentity serve                       DevIdp:* settings below
//   TransactHub.DevIdentity keys --out <dir>            the laptop's signing keys, made once: the studio's package signing key,
//                                                       the backup signing key, the file signing key and the trusted signers
//
// DevIdp:Issuer (the iss of its tokens, the browser's address of it), DevIdp:Audience, DevIdp:Bank (the tenants claim),
// DevIdp:Directory (file: the synthetic users and clients), DevIdp:KeyFile, DevIdp:JwksOut, DevIdp:UserPasswordRef (a reference),
// DevIdp:CorsOrigins (comma separated), DevIdp:TokenMinutes (default 60).

if (args.Length > 0 && args[0] == "keys") return Keys.Make(args);

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsProduction())
{
    Console.Error.WriteLine("The development identity provider is refused in Production: a bank's runtime trusts only the bank's own identity provider (R-09-77).");
    return 2;
}
var cfg = builder.Configuration;
string Need(string k) => cfg[k] is { Length: > 0 } v ? v : throw new InvalidOperationException($"Set {k}");
var issuer = Need("DevIdp:Issuer").TrimEnd('/');
var audience = Need("DevIdp:Audience");
var bank = Need("DevIdp:Bank");
var secrets = SecretResolver.FromEnvironment(k => cfg[k]);
var directory = JsonNode.Parse(File.ReadAllText(Need("DevIdp:Directory"))) as JsonObject ?? throw new InvalidOperationException("DevIdp:Directory is not a JSON object");
var minutes = int.TryParse(cfg["DevIdp:TokenMinutes"], out var m) ? m : 60;
var origins = (cfg["DevIdp:CorsOrigins"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

// The signing key: made on first start, kept in the provider's own volume, never in an image.
var keyFile = Need("DevIdp:KeyFile");
using var rsa = RSA.Create();
if (File.Exists(keyFile)) rsa.ImportFromPem(File.ReadAllText(keyFile));
else
{
    rsa.KeySize = 2048;
    Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
    File.WriteAllText(keyFile, rsa.ExportPkcs8PrivateKeyPem());
    File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}
var p = rsa.ExportParameters(false);
var kid = Jwt.B64(SHA256.HashData(p.Modulus!))[..16];
var jwks = new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["kty"] = "RSA", ["use"] = "sig", ["alg"] = "RS256", ["kid"] = kid, ["n"] = Jwt.B64(p.Modulus!), ["e"] = Jwt.B64(p.Exponent!) }) };
if (cfg["DevIdp:JwksOut"] is { Length: > 0 } jwksOut)
{
    Directory.CreateDirectory(Path.GetDirectoryName(jwksOut)!);
    File.WriteAllText(jwksOut + ".part", jwks.ToJsonString());
    File.Move(jwksOut + ".part", jwksOut, overwrite: true);
}

var users = (directory["users"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(u => (string)u["sub"]!);
var clients = (directory["clients"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(c => (string)c["clientId"]!);
var publicClients = (directory["publicClients"] as JsonArray ?? []).Select(x => (string)x!).ToHashSet();
var codes = new ConcurrentDictionary<string, (string sub, string client, string redirect, string challenge, DateTimeOffset until)>();

string Token(string sub, string? name, JsonArray? groups, JsonArray? roles, string? azp)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var claims = new JsonObject
    {
        ["iss"] = issuer, ["aud"] = audience, ["sub"] = sub, ["iat"] = now, ["nbf"] = now - 30, ["exp"] = now + minutes * 60,
        ["tenants"] = new JsonArray(bank), ["jti"] = Guid.NewGuid().ToString("N"),
    };
    if (name is not null) claims["name"] = name;
    if (groups is not null) claims["groups"] = groups.DeepClone();
    if (roles is not null) claims["roles"] = roles.DeepClone();
    if (azp is not null) claims["azp"] = azp;
    return Jwt.Sign(rsa, kid, claims);
}

var app = builder.Build();
app.Use(async (ctx, next) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && origins.Contains(origin))
    {
        ctx.Response.Headers.AccessControlAllowOrigin = origin;
        ctx.Response.Headers.AccessControlAllowHeaders = "content-type, authorization";
        ctx.Response.Headers.AccessControlAllowMethods = "GET, POST";
        ctx.Response.Headers.Vary = "Origin";
        if (HttpMethods.IsOptions(ctx.Request.Method)) { ctx.Response.StatusCode = 204; return; }
    }
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    await next();
});

app.MapGet("/health", () => Results.Ok(new { ok = true, issuer, kid }));
app.MapGet("/.well-known/openid-configuration", () => Results.Json(new JsonObject
{
    ["issuer"] = issuer, ["authorization_endpoint"] = $"{issuer}/authorize", ["token_endpoint"] = $"{issuer}/token", ["jwks_uri"] = $"{issuer}/jwks",
    ["response_types_supported"] = new JsonArray("code"), ["grant_types_supported"] = new JsonArray("authorization_code", "client_credentials", "password"),
    ["code_challenge_methods_supported"] = new JsonArray("S256"), ["id_token_signing_alg_values_supported"] = new JsonArray("RS256"),
    ["subject_types_supported"] = new JsonArray("public"),
}));
app.MapGet("/jwks", () => Results.Json(jwks));

// The sign-in page: the synthetic users of the directory, one button each. No password; never a real person.
app.MapGet("/authorize", (HttpRequest r) =>
{
    var q = r.Query;
    if (q["response_type"] != "code" || !publicClients.Contains(q["client_id"].ToString()) || q["code_challenge_method"] != "S256" || string.IsNullOrEmpty(q["code_challenge"]))
        return Results.BadRequest(new { error = "invalid_request", error_description = "the code flow with PKCE (S256) of a known public client" });
    if (!Uri.TryCreate(q["redirect_uri"], UriKind.Absolute, out var redirect) || !origins.Contains(redirect.GetLeftPart(UriPartial.Authority)))
        return Results.BadRequest(new { error = "invalid_request", error_description = "the redirect URI is not one of the laptop's consoles" });
    string H(string s) => WebUtility.HtmlEncode(s);
    var hidden = string.Concat(new[] { "client_id", "redirect_uri", "state", "code_challenge" }.Select(k => $"<input type=\"hidden\" name=\"{k}\" value=\"{H(q[k].ToString())}\">"));
    var buttons = string.Concat(users.Values.Select(u => $"<p><button name=\"sub\" value=\"{H((string)u["sub"]!)}\">Sign in as {H((string?)u["name"] ?? (string)u["sub"]!)}</button> <small>{H(string.Join(", ", (u["groups"] as JsonArray ?? []).Select(g => (string?)g)))}</small></p>"));
    var html = $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Development sign-in ({H(bank)})</title></head><body><h1>Development sign-in</h1><p>The laptop's identity provider for {H(bank)}: synthetic users only, never used in production.</p><form method=\"post\" action=\"{H(issuer)}/authorize\">{hidden}{buttons}</form></body></html>";
    return Results.Content(html, "text/html; charset=utf-8");
});
app.MapPost("/authorize", async (HttpRequest r) =>
{
    var f = await r.ReadFormAsync();
    var sub = f["sub"].ToString();
    if (!users.ContainsKey(sub) || !publicClients.Contains(f["client_id"].ToString())) return Results.BadRequest(new { error = "access_denied" });
    var redirect = f["redirect_uri"].ToString();
    if (!Uri.TryCreate(redirect, UriKind.Absolute, out var ru) || !origins.Contains(ru.GetLeftPart(UriPartial.Authority))) return Results.BadRequest(new { error = "invalid_request" });
    var code = Jwt.B64(RandomNumberGenerator.GetBytes(24));
    codes[code] = (sub, f["client_id"].ToString(), redirect, f["code_challenge"].ToString(), DateTimeOffset.UtcNow.AddMinutes(2));
    var sep = redirect.Contains('?') ? '&' : '?';
    return Results.Redirect($"{redirect}{sep}code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(f["state"].ToString())}");
});

app.MapPost("/token", async (HttpRequest r) =>
{
    var f = await r.ReadFormAsync();
    IResult Err(string e, string d, int status = 400) => Results.Json(new { error = e, error_description = d }, statusCode: status);
    IResult Ok(string token) => Results.Json(new { access_token = token, token_type = "Bearer", expires_in = minutes * 60 });
    switch (f["grant_type"].ToString())
    {
        case "authorization_code":
        {
            if (!codes.TryRemove(f["code"].ToString(), out var c) || c.until < DateTimeOffset.UtcNow) return Err("invalid_grant", "unknown or expired code");
            if (c.client != f["client_id"] || c.redirect != f["redirect_uri"]) return Err("invalid_grant", "the code was given to another client or redirect");
            var verifier = f["code_verifier"].ToString();
            if (Jwt.B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != c.challenge) return Err("invalid_grant", "PKCE: the verifier does not match the challenge");
            var u = users[c.sub];
            return Ok(Token(c.sub, (string?)u["name"], u["groups"] as JsonArray, null, c.client));
        }
        case "client_credentials":
        {
            var (id, secret) = (f["client_id"].ToString(), f["client_secret"].ToString());
            if (r.Headers.Authorization.ToString() is { } auth && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..])).Split(':', 2);
                (id, secret) = (Uri.UnescapeDataString(pair[0]), pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "");
            }
            if (!clients.TryGetValue(id, out var client)) return Err("invalid_client", "unknown client", 401);
            var want = (await secrets.Resolve((string)client["secretRef"]!)).Reveal();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(want), Encoding.UTF8.GetBytes(secret))) return Err("invalid_client", "wrong client secret", 401);
            return Ok(Token(id, (string?)client["name"] ?? id, client["groups"] as JsonArray, client["roles"] as JsonArray, id));
        }
        case "password":
        {
            // For the laptop's own tests: a synthetic user, with the one development password of the stack.
            if (cfg["DevIdp:UserPasswordRef"] is not { Length: > 0 } pref) return Err("unsupported_grant_type", "the password grant is off");
            var sub = f["username"].ToString();
            if (!users.TryGetValue(sub, out var u)) return Err("invalid_grant", "unknown user");
            var want = (await secrets.Resolve(pref)).Reveal();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(want), Encoding.UTF8.GetBytes(f["password"].ToString()))) return Err("invalid_grant", "wrong password");
            return Ok(Token(sub, (string?)u["name"], u["groups"] as JsonArray, null, f["client_id"].ToString()));
        }
        default: return Err("unsupported_grant_type", "authorization_code, client_credentials or password");
    }
});

await app.RunAsync();
return 0;

static class Jwt
{
    public static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Sign(RSA key, string kid, JsonObject claims)
    {
        var head = B64(Encoding.UTF8.GetBytes(new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = kid }.ToJsonString()));
        var body = B64(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var sig = key.SignData(Encoding.ASCII.GetBytes($"{head}.{body}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{head}.{body}.{B64(sig)}";
    }
}

/// <summary>
/// The laptop's keys, made once into a volume and never into an image: the studio's package signing key (P-256, PKCS#8), the
/// backup signing key, the front door's file signing key and the trusted signers the runtime reads (the studio's and the backup
/// key's public keys). A key already there is kept, so the stack restarts with the same trust.
/// </summary>
static class Keys
{
    public static int Make(string[] args)
    {
        var i = Array.IndexOf(args, "--out");
        if (i < 0 || i + 1 >= args.Length) { Console.Error.WriteLine("usage: TransactHub.DevIdentity keys --out <dir>"); return 2; }
        var dir = args[i + 1];
        Directory.CreateDirectory(dir);
        ECDsa Key(string name)
        {
            var path = Path.Combine(dir, name);
            var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (File.Exists(path)) k.ImportFromPem(File.ReadAllText(path));
            else
            {
                File.WriteAllText(path, k.ExportPkcs8PrivateKeyPem());
                Console.WriteLine($"made {name} ({ReleaseSignature.KeyIdOf(k)})");
            }
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            return k;
        }
        using var studio = Key("studio-signing.pem");
        using var backup = Key("backup-signing.pem");
        using var files = Key("file-signing.pem");
        var trust = new JsonObject
        {
            ["kind"] = "trusted-signers",
            ["keys"] = new JsonArray(new[] { studio, backup }.Select(k => (JsonNode)new JsonObject { ["keyId"] = ReleaseSignature.KeyIdOf(k), ["publicKey"] = k.ExportSubjectPublicKeyInfoPem() }).ToArray()),
            ["revoked"] = new JsonArray(),
        };
        File.WriteAllText(Path.Combine(dir, "trusted-signers.json"), trust.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"trusted signers: {ReleaseSignature.KeyIdOf(studio)} (studio), {ReleaseSignature.KeyIdOf(backup)} (backups)");
        return 0;
    }
}
