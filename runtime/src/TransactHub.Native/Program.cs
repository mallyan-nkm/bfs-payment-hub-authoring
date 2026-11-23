using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TransactHub.Auth;
using TransactHub.Security;
using TransactHub.Activities.Native;
using TransactHub.Activities.Platform.Posting;
using TransactHub.Activities.Wps.Native;
using TransactHub.Ports;

// The native host (spec R-08-14): the runtime's own code-behind for activities bound `native`. The studio's
// interpreter and every deployed worker call it with the attempt's view, parameters and time:
//   POST /native        { activity, release, view, params, now, tenant, level, instance } -> { kind: result, outcome, reasonCode, writes } | { kind: error, ... }
//   GET  /health        -> the activities it serves
//   GET  /files         -> the files native activities received or produced (a rendered SIF, PRC, DIF or RFA, an encrypted or decrypted file): hash, name, size
//   GET  /files/{sha}   -> one of them, as kept
//   GET  /postings      -> what the core-banking simulator adapter was asked to post, per tenant and binding
// TH_NATIVE_URLS (default http://127.0.0.1:7300) sets where it listens. TH_BANKS_DIR names the banks' configuration
// (content-configurations/banks): the port bindings the platform's core-banking, file and register activities resolve per
// tenant, as of the engine's time (plan P2, R-08-15). Without it no tenant has a port bound, and those activities are refused.
// With TH_STORE_PROVIDER and TH_STORE_CONNECTION every file is also kept in the store (Issue I-97, R-09-62), signed by the key
// TH_FILE_SIGNING_KEY_REF names (a P-256 private key in PEM, by reference); without one the host makes a key for its own lifetime.
//
// Who may call it (R-08-17, Issue I-75): the bank's machine callers, by the runtime's one authentication library
// (TransactHub.Auth): a client-credentials bearer JWT of the bank's issuer whose role map gives `native-caller`, in the tenant
// the attempt names. Configure Auth:Oidc:Issuer, Auth:Oidc:Audience, Auth:Oidc:SigningKeys or discovery, and Auth:RoleMap,
// as for every runtime host. Without them the host answers only on loopback addresses (one machine: a developer's, a
// harness); a host that listens beyond the machine with no authentication does not start. A worker sends its token by
// reference, TH_NATIVE_TOKEN (env:, file:, vault:, akv:, aws-sm:, aws-ssm:).

var builder = WebApplication.CreateBuilder(args);
var urls = builder.Configuration["TH_NATIVE_URLS"] ?? "http://127.0.0.1:7300";
builder.WebHost.UseUrls(urls);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
const string NativeCaller = "native-caller";
var authenticated = !string.IsNullOrEmpty(builder.Configuration["Auth:Oidc:Issuer"]);
if (authenticated)
    await builder.Services.AddRuntimeAuth(builder.Configuration, SecretResolver.FromEnvironment(k => builder.Configuration[k]), [NativeCaller]);
else if (!LoopbackOnly(urls))
    throw new InvalidOperationException($"The native host listens beyond this machine ({urls}) and has no authentication: set Auth:Oidc:Issuer, Auth:Oidc:Audience and Auth:RoleMap granting the {NativeCaller} role to the workers' client credentials (spec R-08-17)");
var app = builder.Build();
app.UseRuntimeSecurityHeaders(app.Configuration, limitWrites: false);
if (authenticated)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
else app.Logger.LogWarning("The native host takes callers without authentication: it listens on loopback only ({Urls}). Configure Auth:Oidc for any other deployment (R-08-17).", urls);

// Every endpoint but /health needs the native-caller role when the host authenticates.
RouteHandlerBuilder Guarded(RouteHandlerBuilder r) => authenticated ? r.RequireAuthorization(NativeCaller) : r;

var served = new ConcurrentDictionary<string, int>();
var banksDir = Environment.GetEnvironmentVariable("TH_BANKS_DIR");
var bindings = banksDir is { Length: > 0 } ? CoreBankingResolver.ReadBanks(banksDir) : new PortBindings();
var coreBanking = new CoreBankingResolver(bindings);
// With TH_STORE_PROVIDER and TH_STORE_CONNECTION the platform's holds are answered here too (p.port.operations-queue.v1 given to
// the runtime): a hold is opened on the payment's chain and answered held; the engine awaits the operator's decision (R-05-50, R-05-56).
var storeProvider = Environment.GetEnvironmentVariable("TH_STORE_PROVIDER");
var storeConnection = Environment.GetEnvironmentVariable("TH_STORE_CONNECTION");
TransactHub.Store.StoreOptions? store = string.IsNullOrEmpty(storeProvider) || string.IsNullOrEmpty(storeConnection) ? null
    : new TransactHub.Store.StoreOptions(Enum.Parse<TransactHub.Store.StoreProvider>(storeProvider, true), storeConnection);
INativeActivity[] holds = store is null ? [] : TransactHub.Operations.StoreHold.For(store);
var signingRef = Environment.GetEnvironmentVariable("TH_FILE_SIGNING_KEY_REF");
var signingKey = signingRef is { Length: > 0 }
    ? TransactHub.Security.ReleaseSignature.PrivateKeyFromPem(await TransactHub.Security.SecretResolver.FromEnvironment().Resolve(signingRef))
    : ECDsa.Create(ECCurve.NamedCurves.nistP256);
var files = new HostFiles(store, signingKey);
Action<string, string, byte[]> keep = (sha, name, content) => files.Keep("out", "rendered", name, content, sha);
INativeActivity[] activities = [new ValidateCbxFile(), new RenderCbSif(keep),
    .. PlatformActivities.CoreBanking(coreBanking), .. OrderActivities.All(), new PostReturn(coreBanking), .. WpsInwardActivities.All(keep),
    .. FileProtectionActivities.All(bindings, files), EnrichEmployerStatus.On(bindings), .. holds];
var byName = activities.ToDictionary(x => x.Name);

var health = app.MapGet("/health", () => Results.Ok(new { activities = byName.Keys.Order(), served, store = store is not null, signingKey = TransactHub.Store.FileSignature.KeyIdOf(signingKey) }));
if (authenticated) health.AllowAnonymous();

Guarded(app.MapPost("/native", async (HttpRequest req, ClaimsPrincipal user) =>
{
    var body = await JsonNode.ParseAsync(req.Body) as JsonObject ?? throw new BadHttpRequestException("not JSON");
    // An attempt for a tenant needs the role in that tenant: one bank's worker never posts to another bank's core banking.
    if (authenticated && (string?)body["tenant"] is { Length: > 0 } forTenant && !RuntimeAuth.HasRoleFor(user, NativeCaller, forTenant))
        return Results.Json(new { kind = "error", name = "NotAuthorised", retryable = false, message = $"{RuntimeAuth.Actor(user)} may not run native activities for {forTenant}" }, statusCode: 403);
    var name = (string?)body["activity"] ?? "";
    if (!byName.TryGetValue(name, out var act))
        return Results.Ok(new { kind = "error", name = "NotNative", retryable = false, message = $"The native host serves no {name}" });
    var now = DateTimeOffset.TryParse((string?)body["now"], out var t) ? t : DateTimeOffset.UtcNow;
    var view = body["view"] as JsonObject ?? new JsonObject();
    var tenant = (string?)body["tenant"];
    try
    {
        // Whose files this attempt keeps: its tenant, payment and leg.
        HostFiles.Current.Value = new HostFiles.Attempt(tenant, NativeContext.S(view, "header.scheme") is { Length: > 0 } sc ? sc : name.Split('.')[0] == "p" ? "p" : string.Join('.', name.Split('.').Take(2)),
            NativeContext.S(view, "header.paymentId") is { Length: > 0 } pid ? pid : null, (string?)body["instance"], name);
        // A reader keeps the file it is given as one received (the inbound message, or the reply an await took).
        if (name.Contains(".map.") && name.Contains("-in.v"))
            foreach (var path in new[] { "messages.lastReceived", "messages.inbound" })
                if (NativeContext.Node(view, path) is JsonObject m && NativeContext.S(m, "payload") is { Length: > 0 } payload)
                {
                    files.Keep("in", "received", NativeContext.S(m, "fileName") is { Length: > 0 } fn ? fn : $"{NativeContext.S(m, "type")}.txt", System.Text.Encoding.UTF8.GetBytes(payload));
                    break;
                }
        var r = act.Run(new NativeAttempt(name, (string?)body["release"], view, body["params"] as JsonObject ?? new JsonObject(), now, tenant, (string?)body["level"]));
        served.AddOrUpdate(name, 1, (_, n) => n + 1);
        // A hold opened: the engine awaits the operator's decision (R-05-56, Issue I-98).
        if (r.Held is { } held) return Results.Ok(new JsonObject { ["kind"] = "held", ["hold"] = (string?)held["hold"], ["expiry"] = (string?)held["expiry"] });
        return Results.Ok(new JsonObject { ["kind"] = "result", ["outcome"] = r.Outcome, ["reasonCode"] = r.ReasonCode, ["writes"] = r.Writes });
    }
    catch (NativeFailure e)
    {
        return Results.Ok(new { kind = "error", name = e.Name, retryable = e.Retryable, message = e.Message });
    }
    catch (Exception e)
    {
        return Results.Ok(new { kind = "error", name = "NativeFailure", retryable = false, message = e.Message });
    }
    finally { HostFiles.Current.Value = null; }
}));

Guarded(app.MapGet("/postings", () => Results.Ok(coreBanking.Built.Where(x => x.CoreBanking is SimulatedCoreBanking).Select(x => new
{
    tenant = x.Binding.Tenant, adapter = x.Binding.Adapter, validFrom = x.Binding.ValidFrom,
    batches = ((SimulatedCoreBanking)x.CoreBanking).Snapshot().Select(p => new { p.Reference, valueDate = p.ValueDate.ToString("yyyy-MM-dd"), legs = p.Legs }),
}))));

Guarded(app.MapGet("/files", () => Results.Ok(files.All.Select(f => new { sha256 = f.Key, name = f.Value.Name, bytes = f.Value.Content.Length, direction = f.Value.Direction, kind = f.Value.Kind }))));

Guarded(app.MapGet("/files/{sha}", (string sha) => files.All.TryGetValue(sha, out var f)
    ? Results.File(f.Content, "text/plain", f.Name)
    : Results.NotFound()));

app.Run();

// Every URL the host listens on is a loopback address (127.0.0.0/8, ::1, localhost).
static bool LoopbackOnly(string urls) => urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(u =>
    Uri.TryCreate(u.Replace("://*", "://wildcard").Replace("://+", "://wildcard"), UriKind.Absolute, out var x)
    && (x.Host == "localhost" || (IPAddress.TryParse(x.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip))));

public partial class Program;

/// <summary>
/// The host's files: in its memory, by SHA-256 (served on <c>/files</c>), and, with a store, in the store's files (Issue I-97), with the
/// attempt's tenant, payment and leg, signed by the host's key. A file the store cannot take fails the attempt: a file sent or received
/// is never left unkept.
/// </summary>
sealed class HostFiles(TransactHub.Store.StoreOptions? store, ECDsa key) : INativeFiles
{
    public sealed record Attempt(string? Tenant, string Scheme, string? PaymentId, string? Instance, string Activity);
    public sealed record Kept(string Name, byte[] Content, string Direction, string Kind);
    public static readonly AsyncLocal<Attempt?> Current = new();
    public readonly ConcurrentDictionary<string, Kept> All = new(StringComparer.OrdinalIgnoreCase);

    public string Keep(string direction, string kind, string fileName, byte[] content) => Keep(direction, kind, fileName, content, null);

    /// <summary>Keeps a file under the hash its builder gave (the source's builders write it in capitals), else its own.</summary>
    public string Keep(string direction, string kind, string fileName, byte[] content, string? given)
    {
        var sha = given ?? TransactHub.Store.FileStore.Sha256Hex(content);
        All[sha] = new Kept(fileName, content, direction, kind);
        var a = Current.Value;
        if (store is not null && a?.Tenant is { Length: > 0 } tenant)
        {
            using var db = TransactHub.Store.StoreDbContext.Open(store);
            new TransactHub.Store.FileStore(db, key).Keep(new TransactHub.Store.FileToKeep(tenant, a.Scheme, direction, kind, fileName, a.PaymentId, a.Instance, a.Activity), content)
                .GetAwaiter().GetResult();
        }
        return sha;
    }

    public byte[]? Get(string sha256) => All.TryGetValue(sha256, out var k) ? k.Content : null;
}
