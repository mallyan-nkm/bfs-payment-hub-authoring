using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TransactHub.Auth;
using TransactHub.Security;
using TransactHub.Activities.Native;
using TransactHub.Activities.Platform.Posting;
using TransactHub.Native;
using TransactHub.Ports;

// The native host (spec R-08-14): the runtime's own code-behind for activities bound `native`. The studio's
// interpreter and every deployed worker call it with the attempt's view, parameters and time:
//   POST /native        { activity, release, distribution, view, params, now, tenant, level, instance } -> { kind: result, outcome, reasonCode, writes } | { kind: error, ... }
//   GET  /health        -> the activities it serves to an attempt that names no distribution, and the native contract it supports
//   GET  /modules       -> the distributions loaded side by side, each its native modules, and those unloaded on removal
//   GET  /files         -> the files native activities received or produced (a rendered SIF, PRC, DIF or RFA, an encrypted or decrypted file): hash, name, size
//   GET  /files/{sha}   -> one of them, as kept
//   GET  /postings      -> what the core-banking simulator adapter was asked to post, per tenant and binding
// TH_NATIVE_URLS (default http://127.0.0.1:7300) sets where it listens. TH_BANKS_DIR names the banks' configuration
// (content-configurations/banks): the port bindings the platform's core-banking, file and register activities resolve per
// tenant, as of the engine's time (plan P2, R-08-15). Without it no tenant has a port bound, and those activities are refused.
// With TH_STORE_PROVIDER and TH_STORE_CONNECTION every file is also kept in the store (Issue I-97, R-09-62), signed by the key
// TH_FILE_SIGNING_KEY_REF names (a P-256 private key in PEM, by reference); without one the host makes a key for its own lifetime.
//
// Side by side (spec R-09-120 to R-09-124): this image holds the platform's native activities only. A scheme's or a bank's native
// activities (WPS's) come in the distribution, built from their code-behind project when the studio built it, each assembly with
// its SHA-256 in the signed bill of materials. An attempt names the distribution its payment is pinned to (the worker's
// TH_DISTRIBUTION); the host loads that distribution's assemblies from the store (TH_STORE_*) into a collectible context of its
// own, checks their hashes and native contract again, and serves the attempt from it by activity and release. A distribution
// removed in every environment it was known in is unloaded (checked every TH_NATIVE_REFRESH, 10 seconds by default). An attempt
// that names none (the studio's interpreter) is served by the modules TH_NATIVE_DEV_MODULES names, if any.
//
// Who may call it (R-08-17, Issue I-75): the bank's machine callers, by the runtime's one authentication library
// (TransactHub.Auth): a client-credentials bearer JWT of the bank's issuer whose role map gives `native-caller`, in the tenant
// the attempt names. Configure Auth:Oidc:Issuer, Auth:Oidc:Audience, Auth:Oidc:SigningKeys or discovery, and Auth:RoleMap,
// as for every runtime host. Without them the host answers only on loopback addresses (one machine: a developer's, a
// harness); a host that listens beyond the machine with no authentication does not start. A worker sends its token by
// reference, TH_NATIVE_TOKEN (env:, file:, vault:, akv:, aws-sm:, aws-ssm:), or gets its own by client credentials
// (TH_NATIVE_TOKEN_URL). With Tls:CertificateRef, Tls:KeyRef and Tls:ClientCaRef the host takes a client certificate signed
// by the bank's CA in place of a token, with the same role per tenant (R-08-21).

var builder = WebApplication.CreateBuilder(args);
var urls = builder.Configuration["TH_NATIVE_URLS"] ?? "http://127.0.0.1:7300";
builder.WebHost.UseUrls(urls);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
const string NativeCaller = "native-caller";
// Mutual TLS, as an option (R-08-21, Issue I-121): Tls:CertificateRef and Tls:KeyRef serve https; Tls:ClientCaRef takes only
// callers with a client certificate that CA signed, and the role map gives them native-caller per tenant as it does a token.
var hostSecrets = SecretResolver.FromEnvironment(k => builder.Configuration[k]);
var tls = await RuntimeTls.FromConfiguration(builder.Configuration, hostSecrets);
if (tls is not null) builder.WebHost.ConfigureKestrel(k => tls.Apply(k));
var authenticated = !string.IsNullOrEmpty(builder.Configuration["Auth:Oidc:Issuer"]) || tls?.ClientCa is not null;
if (authenticated)
    await builder.Services.AddRuntimeAuth(builder.Configuration, hostSecrets, [NativeCaller], tls);
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
// The platform's own native activities: runtime software, in this image. A scheme's or a bank's native activities are not: each
// distribution carries its own build of them, loaded side by side in a context of its own (R-09-122). What a distribution's
// modules give the platform (a scheme's match of a registered party) is read from that distribution's contributions.
IEnumerable<INativeActivity> Platform(IReadOnlyDictionary<string, object> contributed) =>
[
    .. PlatformActivities.CoreBanking(coreBanking), .. OrderActivities.All(),
    .. FileProtectionActivities.All(bindings, files), PartyRegisterOn(bindings, store, contributed), .. holds,
    // The platform's production adapters (R-09-90): screening, notices, outward facts, the business calendar and the directory.
    .. PlatformPortActivities.All(bindings, ReferenceReaderOn(store)),
];
// TH_NATIVE_DEV_MODULES: module build folders (native-module.json and its assemblies) served to attempts that name no
// distribution: the studio's interpreter and the black-box tests on one machine. A payment pinned to a distribution never uses them.
var devModules = (Environment.GetEnvironmentVariable("TH_NATIVE_DEV_MODULES") ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var refreshEvery = TimeSpan.TryParse(Environment.GetEnvironmentVariable("TH_NATIVE_REFRESH"), System.Globalization.CultureInfo.InvariantCulture, out var re) ? re : TimeSpan.FromSeconds(10);
// Where the distributions are read: the host's store (TH_STORE_*), else the runtime's store as every other host names it (Store:*),
// for the distributions only (holds and files stay as TH_STORE_* says).
var distributions = store ?? (builder.Configuration["Store:Provider"] is { Length: > 0 } sp && builder.Configuration["Store:Connection"] is { Length: > 0 } sc
    ? new TransactHub.Store.StoreOptions(Enum.Parse<TransactHub.Store.StoreProvider>(sp, true), sc) : null);
var modules = new NativeModules(Platform, (dist, _) => new HostServices(dist, keep, bindings, coreBanking), distributions, devModules, refreshEvery);
var development = await modules.For(null);
app.Lifetime.ApplicationStopping.Register(modules.Dispose);

var health = app.MapGet("/health", () => Results.Ok(new { activities = development.ByName.Keys.Order(), served, store = store is not null, signingKey = TransactHub.Store.FileSignature.KeyIdOf(signingKey), contract = NativeContract.Version }));
if (authenticated) health.AllowAnonymous();

// The distributions loaded side by side, each its modules (scope, assembly, SHA-256, activities) and what it served, and those
// unloaded on removal with whether their context has been collected (R-09-122).
Guarded(app.MapGet("/modules", () => Results.Ok(modules.Describe())));

Guarded(app.MapPost("/native", async (HttpRequest req, ClaimsPrincipal user) =>
{
    var body = await JsonNode.ParseAsync(req.Body) as JsonObject ?? throw new BadHttpRequestException("not JSON");
    // An attempt for a tenant needs the role in that tenant: one bank's worker never posts to another bank's core banking.
    if (authenticated && (string?)body["tenant"] is { Length: > 0 } forTenant && !RuntimeAuth.HasRoleFor(user, NativeCaller, forTenant))
        return Results.Json(new { kind = "error", name = "NotAuthorised", retryable = false, message = $"{RuntimeAuth.Actor(user)} may not run native activities for {forTenant}" }, statusCode: 403);
    var name = (string?)body["activity"] ?? "";
    // Dispatched by the payment's pinned distribution, then by activity and release (R-09-122).
    var distribution = (string?)body["distribution"];
    INativeActivity act;
    ActivityTable table;
    try
    {
        table = await modules.For(distribution, req.HttpContext.RequestAborted);
        act = table.Find(name, (string?)body["release"]);
    }
    catch (NativeRefusal e) { return Results.Ok(new { kind = "error", name = e.Name, retryable = false, message = e.Message }); }
    // The store could not be read, or an assembly could not be loaded: the step's retry policy tries again.
    catch (Exception e) when (e is not OperationCanceledException) { return Results.Ok(new { kind = "error", name = "TransientError", retryable = true, message = $"{distribution} could not be loaded: {e.GetType().Name}: {e.Message}" }); }
    var now =DateTimeOffset.TryParse((string?)body["now"], out var t) ? t : DateTimeOffset.UtcNow;
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
        table.Served.AddOrUpdate(name, 1, (_, n) => n + 1);
        // A hold opened: the engine awaits the operator's decision (R-05-56, Issue I-98).
        if (r.Held is { } held) return Results.Ok(new JsonObject { ["kind"] = "held", ["hold"] = (string?)held["hold"], ["expiry"] = (string?)held["expiry"], ["expiryCalendar"] = (string?)held["expiryCalendar"], ["calendar"] = held["calendar"]?.DeepClone() });
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

// p.enrich.party-register.v1 (R-12-27): the bank's party register, read from the store's registers as of the step when the host
// has a store (adapter `register`); WPS matches what it found with the source's MatchCorporate.
static INativeActivity PartyRegisterOn(PortBindings bindings, TransactHub.Store.StoreOptions? store, IReadOnlyDictionary<string, object> contributed)
{
    // A scheme's own match, from its distribution's module (WPS: the source's MatchCorporate).
    var matchers = contributed.Where(kv => kv.Key.StartsWith("party-matcher:", StringComparison.Ordinal) && kv.Value is PartyMatcher)
        .ToDictionary(kv => kv.Key["party-matcher:".Length..], kv => (PartyMatcher)kv.Value, StringComparer.Ordinal);
    RegisterReader? reader = null;
    if (store is not null)
    {
        var regs = new TransactHub.Operations.Registers.RegisterService(store, TransactHub.Operations.Registers.RegisterCatalog.Load());
        reader = async (tenant, kind, field, value, at, ct) => kind is { Length: > 0 } && regs.Catalog.Find(kind)?.Key == field
            ? (await regs.InForce(tenant, kind, value, at, ct)) is { } one ? [one] : []
            : await regs.FindBy(tenant, kind, field, value, at, ct);
    }
    return EnrichPartyRegister.On(bindings, reader, matchers);
}

// The reference tables the bank's configuration installed, for the `reference` adapters of the calendar and the directory (R-09-90).
static ReferenceReader? ReferenceReaderOn(TransactHub.Store.StoreOptions? store) => store is null ? null : async (tenant, domain, at, ct) =>
{
    await using var db = TransactHub.Store.StoreDbContext.Open(store);
    return await TransactHub.Store.ReferenceTables.RowsAt(db, tenant, domain, at, ct);
};

// Every URL the host listens on is a loopback address (127.0.0.0/8, ::1, localhost).
static bool LoopbackOnly(string urls) => urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(u =>
    Uri.TryCreate(u.Replace("://*", "://wildcard").Replace("://+", "://wildcard"), UriKind.Absolute, out var x)
    && (x.Host == "localhost" || (IPAddress.TryParse(x.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip))));

public partial class Program;

/// <summary>What a distribution's native module is given (R-09-122): the host's file keeping, the bank's port bindings and core banking.</summary>
sealed class HostServices(string? distribution, Action<string, string, byte[]> keep, PortBindings bindings, TransactHub.Activities.Platform.Posting.CoreBankingResolver coreBanking) : INativeServices
{
    public string? Distribution => distribution;
    public void KeepRendered(string sha256, string fileName, byte[] content) => keep(sha256, fileName, content);
    public T? Service<T>() where T : class => typeof(T) == typeof(PortBindings) ? bindings as T : typeof(T) == typeof(TransactHub.Activities.Platform.Posting.CoreBankingResolver) ? coreBanking as T : null;
    public void Contribute(string key, object value) => throw new InvalidOperationException("A module contributes through its distribution's table");
}

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
