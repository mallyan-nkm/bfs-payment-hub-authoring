using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using TransactHub.Auth;
using TransactHub.Security;
using TransactHub.Activities.Native;
using TransactHub.Activities.Platform.Posting;
using TransactHub.Activities.Wps.Native;
using TransactHub.Ports;

// The native host (spec R-08-14): the runtime's own code-behind for activities bound `native`. The studio's
// interpreter and every deployed worker call it with the attempt's view, parameters and time:
//   POST /native        { activity, release, view, params, now } -> { kind: result, outcome, reasonCode, writes } | { kind: error, ... }
//   GET  /health        -> the activities it serves
//   GET  /files         -> the files native activities produced (a rendered SIF, PRC, DIF or RFA): hash, name, size
//   GET  /files/{sha}   -> one of them, as kept
//   GET  /postings      -> what the core-banking simulator adapter was asked to post, per tenant and binding
// TH_NATIVE_URLS (default http://127.0.0.1:7300) sets where it listens. TH_BANKS_DIR names the banks' configuration
// (content-configurations/banks): the port bindings the platform's core-banking activities resolve per tenant, as of the
// engine's time (plan P2, R-08-15). Without it no tenant has core banking bound, and those activities are refused.
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

var files = new ConcurrentDictionary<string, (string name, byte[] content)>();
var served = new ConcurrentDictionary<string, int>();
var banksDir = Environment.GetEnvironmentVariable("TH_BANKS_DIR");
var coreBanking = new CoreBankingResolver(banksDir is { Length: > 0 } ? CoreBankingResolver.ReadBanks(banksDir) : new PortBindings());
// With TH_STORE_PROVIDER and TH_STORE_CONNECTION the platform's holds are answered here too (p.port.operations-queue.v1 given to
// the runtime): a hold is opened on the payment's chain and answered held; the engine awaits the operator's decision (R-05-50, R-05-56).
var storeProvider = Environment.GetEnvironmentVariable("TH_STORE_PROVIDER");
var storeConnection = Environment.GetEnvironmentVariable("TH_STORE_CONNECTION");
INativeActivity[] holds = string.IsNullOrEmpty(storeProvider) || string.IsNullOrEmpty(storeConnection) ? []
    : TransactHub.Operations.StoreHold.For(new TransactHub.Store.StoreOptions(Enum.Parse<TransactHub.Store.StoreProvider>(storeProvider, true), storeConnection));
Action<string, string, byte[]> keep = (sha, name, content) => files[sha] = (name, content);
INativeActivity[] activities = [new ValidateCbxFile(), new RenderCbSif(keep),
    .. PlatformActivities.CoreBanking(coreBanking), new PostReturn(coreBanking), .. WpsInwardActivities.All(keep), .. holds];
var byName = activities.ToDictionary(x => x.Name);

var health = app.MapGet("/health", () => Results.Ok(new { activities = byName.Keys.Order(), served }));
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
    try
    {
        var r = act.Run(new NativeAttempt(name, (string?)body["release"], body["view"] as JsonObject ?? new JsonObject(), body["params"] as JsonObject ?? new JsonObject(), now,
            (string?)body["tenant"], (string?)body["level"]));
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
}));

Guarded(app.MapGet("/postings", () => Results.Ok(coreBanking.Built.Where(x => x.CoreBanking is SimulatedCoreBanking).Select(x => new
{
    tenant = x.Binding.Tenant, adapter = x.Binding.Adapter, validFrom = x.Binding.ValidFrom,
    batches = ((SimulatedCoreBanking)x.CoreBanking).Snapshot().Select(p => new { p.Reference, valueDate = p.ValueDate.ToString("yyyy-MM-dd"), legs = p.Legs }),
}))));

Guarded(app.MapGet("/files", () => Results.Ok(files.Select(f => new { sha256 = f.Key, name = f.Value.name, bytes = f.Value.content.Length }))));

Guarded(app.MapGet("/files/{sha}", (string sha) => files.TryGetValue(sha, out var f)
    ? Results.File(f.content, "text/plain", f.name)
    : Results.NotFound()));

app.Run();

// Every URL the host listens on is a loopback address (127.0.0.0/8, ::1, localhost).
static bool LoopbackOnly(string urls) => urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(u =>
    Uri.TryCreate(u.Replace("://*", "://wildcard").Replace("://+", "://wildcard"), UriKind.Absolute, out var x)
    && (x.Host == "localhost" || (IPAddress.TryParse(x.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip))));

public partial class Program;

