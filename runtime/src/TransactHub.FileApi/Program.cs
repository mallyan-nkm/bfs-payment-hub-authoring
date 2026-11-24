using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Auth;
using TransactHub.FileApi;
using TransactHub.Ports;
using TransactHub.Security;
using TransactHub.Store;
using TransactHub.Telemetry;

// The File API (R-09-82 to R-09-86; owner's decision of 23 November 2026): the one runtime service every file passes through.
//
//   POST /files                 a channel's or a corporate's upload (system clients of the bank's identity provider, role file-sender),
//                               or a file the SFTP gateway hands in (role file-gateway): JSON { tenant, message, fileName,
//                               contentBase64, correlation? } or a multipart form (file, tenant, message). Scheme-neutral: the
//                               source's /api/wps/UploadSIFFile is POST /files with message uae.wps.sif.
//   POST /files/ui              a person's upload from the bank's own screen (role file-uploader), a multipart form; the source's
//                               /api/wps/ui/upload-sif-file.
//   GET  /files?tenant&paymentId, GET /files/{id}, GET /files/{id}/content     a payment's files, one file and its facts, its bytes
//                               (role file-reader); the operations console's documents tab reads the same rows.
//   POST /outbox                a file a workflow sends (the native host's exchanges, role file-outbox): kept, queued and delivered.
//   POST /files/{id}/resend     the same kept bytes again (the operations and service consoles, role file-resender).
//   POST /transfer              a read, a listing or a removal on the bank's binding (role file-outbox): the hosts never reach a
//                               bank's folder or SFTP partner themselves (R-09-138).
//
// Settings: Store:Provider, Store:Connection; FileApi:Environment (the environment whose active distribution routes a file);
// the bank's port bindings are those of its configuration release in force, read from the store at each use (R-09-112, I-158 (2));
// FileApi:EngineHosts:<bank> (the engine host's supervisor of each bank) and
// FileApi:Workers:<scope> (the worker that runs a scope's intake maps): without an engine host the front door is off and a file is
// checked and kept only; FileApi:MaxBytes, FileApi:Messages:<message>:*, FileApi:Inboxes:*, FileApi:PollSeconds; TH_FILE_SIGNING_KEY_REF;
// Auth:* as every runtime host (RuntimeAuth).

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var provider = Enum.Parse<StoreProvider>(cfg["Store:Provider"] ?? "SqlServer", ignoreCase: true);
var store = new StoreOptions(provider, cfg["Store:Connection"] ?? throw new InvalidOperationException("Set Store:Connection"));
// Its breakers' moves (its SFTP partners') in the store, so the service console's health shows an open one (R-09-156, Issue I-255).
CircuitBreakers.Shared.Journal = new TransactHub.Operations.StoreBreakerJournal(store, $"{Environment.MachineName}/file-api");
var environment = cfg["FileApi:Environment"] ?? throw new InvalidOperationException("Set FileApi:Environment: the environment whose active distribution a file is routed by");
var secrets = SecretResolver.FromEnvironment(k => cfg[k]);
var fileKey = cfg["TH_FILE_SIGNING_KEY_REF"] is { Length: > 0 } fileKeyRef
    ? ReleaseSignature.PrivateKeyFromPem(await secrets.Resolve(fileKeyRef))
    : System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
// The bank's port bindings: those of its configuration release in force, read live from the store, so a binding a new
// configuration release brings reaches the inboxes, the outbox and the decryption with no restart (R-09-112). Never a folder.
if (cfg["TH_BANKS_DIR"] is { Length: > 0 }) throw new InvalidOperationException("TH_BANKS_DIR names a folder of port bindings, but the File API reads the bank's bindings only from the configuration release in force (spec R-09-112, R-09-113): remove TH_BANKS_DIR and install the bank's configuration release");
var bindings = PortBindings.Live(() => ConfigurationBindings.Load(store, cfg["TH_BANK"] is { Length: > 0 } own ? own : null)
    .Select(b => new PortBinding(b.Tenant, b.Port, b.Adapter, b.ValidFrom, b.Settings, b.Release)).ToList());
var ports = new BoundTransfers(bindings, secrets);
// The bindings now, or none while the store cannot be read (the next poll reads them again).
IReadOnlyList<PortBinding> Bound()
{
    try { return bindings.All; }
    catch (PortBindingException) { return []; }
}
var hostToken = cfg["FileApi:EngineHostTokenRef"] is { Length: > 0 } tr ? (await secrets.Resolve(tr)).Reveal() : null;
var engineHosts = cfg.GetSection("FileApi:EngineHosts").GetChildren().ToDictionary(c => c.Key, c => c.Value ?? "", StringComparer.Ordinal);
var workers = cfg.GetSection("FileApi:Workers").GetChildren().ToDictionary(c => c.Key, c => c.Value ?? "", StringComparer.Ordinal);
// One runtime serves one bank (R-09-76): FileApi:EngineHost names its supervisor.
if (cfg["FileApi:EngineHost"] is { Length: > 0 } oneHost && cfg["TH_BANK"] is { Length: > 0 } oneBank) engineHosts[oneBank] = oneHost;
var frontDoor = engineHosts.Count == 0 ? null : new RuntimeFrontDoor(store,
    // A worker's URL may name {distribution} and {scope}: an installation whose workers are per distribution (the laptop's workers
    // service, R-09-105) runs the intake map on the workers of the distribution the payment is pinned to.
    new WorkerIntake((dist, scope) => (workers.GetValueOrDefault(scope) ?? workers.GetValueOrDefault("*"))?
        .Replace("{distribution}", Uri.EscapeDataString(dist)).Replace("{scope}", Uri.EscapeDataString(scope))),
    new HttpEngineHost(bank => engineHosts.GetValueOrDefault(bank), hostToken));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new FileIntake(store, fileKey, cfg, new Decryptor(bindings, secrets), frontDoor, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new FileOutbox(store, fileKey, ports, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ITransferPorts>(ports);
builder.Services.AddSingleton(sp => new InboxPoller(ports, sp.GetRequiredService<FileIntake>(), environment,
    // The inboxes of the binding in force at each poll: a new configuration release's binding is polled with no restart (R-09-112).
    () => Bound().Where(b => b.Port == FileTransferAdapters.Port).GroupBy(b => b.Tenant).SelectMany(g =>
    {
        try { return Inbox.Of(bindings.InForce(g.Key, FileTransferAdapters.Port, DateTimeOffset.UtcNow)); }
        catch (PortBindingException) { return []; }
    }).Concat(Inbox.Of(cfg)).ToList(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("inbox"), cfg["TH_BANK"]));
// The orders reconciliation breaks start (R-09-250, Issue I-168): only where the front door starts payments.
if (frontDoor is not null)
    builder.Services.AddSingleton(sp => new BreakOrderStarter(store, sp.GetRequiredService<FileIntake>(), environment, cfg["TH_BANK"] is { Length: > 0 } b ? b : null, sp.GetRequiredService<TimeProvider>()));
if (cfg["FileApi:Worker"] != "off") builder.Services.AddHostedService<FileApiWorker>();
await builder.Services.AddRuntimeAuth(cfg, secrets, FileApiAuth.Roles);
builder.Services.AddTransactHubTelemetry("transacthub-file-api");
builder.Services.AddHealthChecks();
var app = builder.Build();
app.UseRuntimeSecurityHeaders(app.Configuration, limitWrites: false);
app.MapTransactHubHealth();
app.UseAuthentication();
app.UseAuthorization();

if (cfg["Store:Migrate"] != "false")
{
    await using var db = StoreDbContext.Open(store);
    await db.Database.MigrateAsync();
}

static IResult Problem(int status, string message, string? code = null) => Results.Json(new { error = message, code }, statusCode: status);

/// <summary>The status a refusal answers with: a duplicate conflicts, a file that fails its checks is unprocessable, a paused intake is locked.</summary>
static int StatusOf(string? code) => code switch
{
    "file.Duplicate" or "file.DuplicateName" => 409,
    "file.TooLarge" => 413,
    "ops.IntakePaused" => 423,
    "frontdoor.NoActiveDistribution" or "frontdoor.NoEngineHost" or "frontdoor.NoWorker" or "frontdoor.EngineRefused" => 503,
    _ => 422,
};

static IResult Answer(Received r) => r.Accepted
    ? Results.Json(new { status = r.Status, file = r.FileId, sha256 = r.Sha256, decrypted = r.DecryptedFileId, payment = r.FrontDoor?.PaymentId, disposition = r.FrontDoor?.Disposition, distribution = r.FrontDoor?.Distribution, detail = r.Detail }, statusCode: 202)
    : Results.Json(new { status = r.Status, file = r.FileId, sha256 = r.Sha256, code = r.Code, error = r.Detail }, statusCode: StatusOf(r.Code));

async Task<(Arrival? Arrival, IResult? Error)> Read(HttpRequest req, ClaimsPrincipal user, string channel)
{
    string? tenant, message, name;
    byte[] content;
    Dictionary<string, string>? correlation = null;
    if (req.HasFormContentType)
    {
        var form = await req.ReadFormAsync();
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null) return (null, Problem(400, "The form carries no file"));
        (tenant, message, name) = (form["tenant"].FirstOrDefault(), form["message"].FirstOrDefault(), file.FileName);
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        content = ms.ToArray();
    }
    else
    {
        var body = await JsonNode.ParseAsync(req.Body) as JsonObject;
        (tenant, message, name) = ((string?)body?["tenant"], (string?)body?["message"], (string?)body?["fileName"]);
        try { content = Convert.FromBase64String((string?)body?["contentBase64"] ?? ""); }
        catch (FormatException) { return (null, Problem(400, "contentBase64 is not Base64")); }
        if (body?["correlation"] is JsonObject c) correlation = c.Where(kv => kv.Value is JsonValue).ToDictionary(kv => kv.Key, kv => kv.Value!.ToString(), StringComparer.Ordinal);
        if ((string?)body?["channel"] is { Length: > 0 } ch) channel = ch;
    }
    if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(message) || string.IsNullOrEmpty(name)) return (null, Problem(400, "Name the tenant, the message and the file's name"));
    // One runtime serves one bank (R-09-76): another bank's file is refused, whatever the caller's roles.
    if (cfg["TH_BANK"] is { Length: > 0 } bank && tenant != bank) return (null, Problem(403, $"This runtime serves {bank}, not {tenant}"));
    try { FileTransferAdapters.Relative("in", Path.GetFileName(name)); }
    catch (PortBindingException e) { return (null, Problem(400, e.Message)); }
    return (new Arrival(tenant, environment, message, Path.GetFileName(name), content, channel, RuntimeAuth.Actor(user), correlation), null);
}

app.MapPost("/files", async (HttpRequest req, ClaimsPrincipal user, FileIntake intake, CancellationToken ct) =>
{
    var (a, error) = await Read(req, user, "api");
    if (error is not null) return error;
    // A file the SFTP gateway hands in is the gateway's; any other is a channel's or a corporate's upload.
    var role = a!.Channel == "sftp" ? FileApiAuth.Gateway : FileApiAuth.Sender;
    if (!RuntimeAuth.HasRoleFor(user, role, a.Tenant)) return Problem(403, $"{RuntimeAuth.Actor(user)} may not send files of {a.Tenant} as {a.Channel}");
    return Answer(await intake.Receive(a, ct));
}).RequireAuthorization().DisableAntiforgery();

app.MapPost("/files/ui", async (HttpRequest req, ClaimsPrincipal user, FileIntake intake, CancellationToken ct) =>
{
    var (a, error) = await Read(req, user, "ui");
    if (error is not null) return error;
    if (!RuntimeAuth.HasRoleFor(user, FileApiAuth.Uploader, a!.Tenant)) return Problem(403, $"{RuntimeAuth.Actor(user)} may not upload files of {a.Tenant}");
    return Answer(await intake.Receive(a with { Channel = "ui" }, ct));
}).RequireAuthorization(FileApiAuth.Uploader).DisableAntiforgery();

app.MapGet("/files", async (string tenant, string paymentId, ClaimsPrincipal user, CancellationToken ct) =>
{
    if (!RuntimeAuth.HasRoleFor(user, FileApiAuth.Reader, tenant)) return Problem(403, $"{RuntimeAuth.Actor(user)} may not read the files of {tenant}");
    await using var db = StoreDbContext.Open(store);
    var rows = await new FileStore(db, fileKey).Of(tenant, paymentId, ct);
    return Results.Ok(rows.Select(r => new { r.Id, r.FileName, r.Direction, r.Kind, r.Size, sha256 = r.ContentHash, r.Leg, r.Activity, r.At, r.SigningKeyId, r.Signature, verified = FileSignature.Verify(r, fileKey) }));
}).RequireAuthorization(FileApiAuth.Reader);

app.MapGet("/files/{id}", async (string id, ClaimsPrincipal user, FileOutbox outbox, CancellationToken ct) =>
{
    await using var db = StoreDbContext.Open(store);
    var r = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    if (r is null || !RuntimeAuth.HasRoleFor(user, FileApiAuth.Reader, r.Tenant)) return Problem(404, $"No file {id}");
    return Results.Ok(new { r.Id, r.Tenant, r.FileName, r.Direction, r.Kind, r.Size, sha256 = r.ContentHash, payment = r.SubjectId, r.Leg, r.At, facts = await outbox.History(id, ct) });
}).RequireAuthorization(FileApiAuth.Reader);

app.MapGet("/files/{id}/content", async (string id, ClaimsPrincipal user, CancellationToken ct) =>
{
    await using var db = StoreDbContext.Open(store);
    var r = await new FileStore(db, fileKey).Read(id, ct);
    if (r is null || !RuntimeAuth.HasRoleFor(user, FileApiAuth.Reader, r.Value.File.Tenant)) return Problem(404, $"No file {id}");
    return Results.File(r.Value.Content, "application/octet-stream", r.Value.File.FileName);
}).RequireAuthorization(FileApiAuth.Reader);

app.MapPost("/outbox", async (OutboxRequest o, ClaimsPrincipal user, FileOutbox outbox, CancellationToken ct) =>
{
    if (!RuntimeAuth.HasRoleFor(user, FileApiAuth.Outbox, o.Tenant)) return Problem(403, $"{RuntimeAuth.Actor(user)} may not send files of {o.Tenant}");
    byte[] content;
    try { content = Convert.FromBase64String(o.ContentBase64); }
    catch (FormatException) { return Problem(400, "contentBase64 is not Base64"); }
    StoredFileRow row;
    try { row = await outbox.Queue(new Outbound(o.Tenant, o.Scheme, o.Kind, o.Folder, o.FileName, content, o.PaymentId, o.Leg, o.Activity, RuntimeAuth.Actor(user)), ct); }
    catch (PortBindingException e) { return Problem(400, e.Message); }
    var d = await outbox.Deliver(row.Id, ct);
    return Results.Json(new { file = row.Id, sha256 = row.ContentHash, delivery = d.Result, adapter = d.Adapter, attempt = d.Attempt, error = d.Error }, statusCode: d.Result == "failed" ? 202 : 201);
}).RequireAuthorization(FileApiAuth.Outbox);

// A read, a listing or a removal on the bank's binding, for a host whose files go through the File API (R-09-138): a file-bulk core
// banking reads its file back, a gateway's answer is collected. The host never opens the bank's folder or SFTP session itself.
app.MapPost("/transfer", async (TransferRequest t, ClaimsPrincipal user, ITransferPorts ports, CancellationToken ct) =>
{
    if (!RuntimeAuth.HasRoleFor(user, FileApiAuth.Outbox, t.Tenant)) return Problem(403, $"{RuntimeAuth.Actor(user)} may not reach the files of {t.Tenant}");
    try
    {
        var (transfer, binding) = ports.For(t.Tenant, DateTimeOffset.UtcNow);
        switch (t.Op)
        {
            case "get":
                var content = await transfer.Get(t.Folder, t.FileName ?? throw new PortBindingException("get names a file"), ct);
                return Results.Ok(new { adapter = binding.Adapter, found = content is not null, contentBase64 = content is null ? null : Convert.ToBase64String(content) });
            case "list":
                return Results.Ok(new { adapter = binding.Adapter, names = await transfer.List(t.Folder, ct) });
            case "delete":
                return Results.Ok(new { adapter = binding.Adapter, deleted = await transfer.Delete(t.Folder, t.FileName ?? throw new PortBindingException("delete names a file"), ct) });
            default:
                return Problem(400, $"op is get, list or delete, not {t.Op}");
        }
    }
    catch (PortBindingException e) { return Problem(400, e.Message); }
}).RequireAuthorization(FileApiAuth.Outbox);

app.MapPost("/files/{id}/resend", async (string id, ResendRequest r, ClaimsPrincipal user, FileOutbox outbox, CancellationToken ct) =>
{
    await using (var db = StoreDbContext.Open(store))
    {
        var f = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null || !RuntimeAuth.HasRoleFor(user, FileApiAuth.Resender, f.Tenant)) return Problem(404, $"No file {id}");
    }
    try
    {
        var d = await outbox.Resend(id, r.OnBehalfOf is { Length: > 0 } who ? $"{RuntimeAuth.Actor(user)} for {who}" : RuntimeAuth.Actor(user), r.ActionId ?? Guid.NewGuid().ToString("N"), ct);
        return Results.Json(new { file = id, result = d.Result, adapter = d.Adapter, attempt = d.Attempt, error = d.Error }, statusCode: d.Result == "failed" ? 502 : 200);
    }
    catch (InvalidOperationException e) { return Problem(409, e.Message); }
}).RequireAuthorization(FileApiAuth.Resender);

app.Run();

/// <summary>A file a workflow sends.</summary>
public sealed record OutboxRequest(string Tenant, string Scheme, string Kind, string Folder, string FileName, string ContentBase64, string? PaymentId = null, string? Leg = null, string? Activity = null);
/// <summary>A read, a listing or a removal on the bank's binding of its file transfer (R-09-138).</summary>
public sealed record TransferRequest(string Tenant, string Op, string Folder, string? FileName = null);
/// <summary>A resend: the console's action id (the same id sends once) and the person it acts for.</summary>
public sealed record ResendRequest(string? ActionId = null, string? OnBehalfOf = null);

public partial class Program;
