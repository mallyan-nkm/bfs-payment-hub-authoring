using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using TransactHub.Operations;
using TransactHub.Store;

namespace TransactHub.Operations.Api;

/// <summary>
/// The File API the upload page posts to (<c>Operations:FileApi:Url</c>), and the address of the service console whose runbooks a
/// refusal's code links to (<c>Operations:ServiceConsole</c>; by default the same origin, where one gateway serves both).
/// </summary>
public sealed class FileApiUploads(Uri? url, string? serviceConsole, HttpMessageHandler? handler = null)
{
    readonly HttpClient http = handler is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(2) } : new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
    public Uri? Url { get; } = url;
    public string RunbookOf(string code) => $"{serviceConsole?.TrimEnd('/') ?? ""}/#/service/errors/{Uri.EscapeDataString(code)}";

    public static FileApiUploads FromConfiguration(IConfiguration cfg) =>
        new(cfg["Operations:FileApi:Url"] is { Length: > 0 } u ? new Uri(u.EndsWith('/') ? u : u + "/") : null, cfg["Operations:ServiceConsole"]);

    /// <summary>The person's upload, as the File API's <c>POST /files/ui</c> takes it: a multipart form, with the person's own token.</summary>
    public async Task<(int Status, JsonObject Body)> Send(string? authorization, string tenant, string message, string fileName, byte[] content, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(tenant), "tenant");
        form.Add(new StringContent(message), "message");
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Url!, "files/ui")) { Content = form };
        if (authorization is { Length: > 0 }) req.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonObject body;
        try { body = JsonNode.Parse(text) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { body = new JsonObject { ["error"] = text.Length > 300 ? text[..300] : text }; }
        if (body["error"] is null && !res.IsSuccessStatusCode) body["error"] = $"The File API answered {(int)res.StatusCode}";
        return ((int)res.StatusCode, body);
    }
}

/// <summary>
/// The operations console's upload page (R-05-73, R-05-74, Issue I-201) under <c>/ops/api/upload</c>: the tenant (the runtime's
/// bank), the messages the distribution active in the console's environment receives (<c>Operations:Environment</c>), and the
/// person's file, posted to the File API's <c>POST /files/ui</c> with the person's own token, so the File API decides by its
/// own role (<c>file-uploader</c>) and checks. The answer is the File API's, with the runbook of its code and the payment it
/// started. The console keeps nothing of the file.
/// </summary>
public static class UploadEndpoints
{
    static IResult Problem(int status, string message, string? code = null) => Results.Json(new { error = message, code }, statusCode: status);
    static OperatorIdentity? Who(HttpContext http) => http.RequestServices.GetRequiredService<IOperatorIdentityResolver>().Resolve(http.User);

    /// <summary>The banks a caller may upload for: the runtime's bank (TH_BANK) when it serves one, else the tenants they are an operator of.</summary>
    static List<string> Banks(OperatorIdentity who, IConfiguration cfg) =>
        cfg["TH_BANK"] is { Length: > 0 } bank ? (who.Has(bank, OperatorRoles.Operator) ? [bank] : []) : who.Tenants.Where(t => t != "*" && who.Has(t, OperatorRoles.Operator)).Order(StringComparer.Ordinal).ToList();

    public static void MapUpload(this WebApplication app, StoreOptions store)
    {
        var api = app.MapGroup("/ops/api");
        var cfg = app.Configuration;

        api.MapGet("/upload", async (HttpContext http, FileApiUploads files) =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var banks = Banks(who, cfg);
            if (banks.Count == 0) return Problem(403, "Uploading a file needs the operator's role in the runtime's bank", OpsCodes.Forbidden);
            if (cfg["Operations:Environment"] is not { Length: > 0 } env) return Problem(503, "Set Operations:Environment: the environment whose active distribution says which messages a file may be", "ops.UploadNotConfigured");
            var targets = new List<UploadTarget>();
            foreach (var b in banks) targets.Add(await UploadCatalogue.Of(store, b, env, http.RequestAborted));
            return Results.Ok(new { fileApi = files.Url is not null, environment = env, targets });
        });

        api.MapPost("/upload", async (HttpContext http, FileApiUploads files) =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            if (files.Url is null) return Problem(503, "Set Operations:FileApi:Url: the upload page posts to the File API", "ops.UploadNotConfigured");
            if (cfg["Operations:Environment"] is not { Length: > 0 } env) return Problem(503, "Set Operations:Environment", "ops.UploadNotConfigured");
            if (!http.Request.HasFormContentType) return Problem(400, "Send a form: tenant, message and file");
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var tenant = form["tenant"].ToString();
            var message = form["message"].ToString();
            var file = form.Files.GetFile("file");
            if (tenant.Length == 0 || message.Length == 0 || file is null) return Problem(400, "Name the tenant and the message, and choose the file");
            if (!Banks(who, cfg).Contains(tenant)) return Problem(403, $"{who.DisplayName} may not upload files of {tenant}", OpsCodes.Forbidden);
            var target = await UploadCatalogue.Of(store, tenant, env, http.RequestAborted);
            if (target.Distribution is null) return Problem(409, $"No distribution of {tenant} is active in {env}", "frontdoor.NoActiveDistribution");
            if (!target.Messages.Any(m => m.Id == message)) return Problem(400, $"The active distribution {target.Distribution} receives no message {message}", "frontdoor.UnknownMessage");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, http.RequestAborted);
            int status;
            JsonObject body;
            try { (status, body) = await files.Send(http.Request.Headers.Authorization.ToString(), tenant, message, Path.GetFileName(file.FileName), ms.ToArray(), http.RequestAborted); }
            catch (HttpRequestException e) { return Problem(502, $"The File API did not answer: {e.Message}", "ops.FileApiUnreachable"); }
            // What became of the file, in the page's words: refused, kept, or kept and its payment started.
            var outcome = status >= 400 || (string?)body["status"] == "refused" ? "refused" : body["payment"] is JsonValue ? "started" : "kept";
            body["outcome"] = outcome;
            body["httpStatus"] = status;
            if ((string?)body["code"] is { Length: > 0 } code) body["runbook"] = files.RunbookOf(code);
            return Results.Json(body, statusCode: status is >= 200 and < 300 ? 200 : status);
        }).DisableAntiforgery();
    }
}
