using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TransactHub.Activities.Wps.Native;

// The native host (spec R-08-14): the runtime's own code-behind for activities bound `native`. The studio's
// interpreter and every deployed worker call it with the attempt's view, parameters and time:
//   POST /native        { activity, release, view, params, now } -> { kind: result, outcome, reasonCode, writes } | { kind: error, ... }
//   GET  /health        -> the activities it serves
//   GET  /files         -> the files native activities produced (a rendered SIF): hash, name, size
//   GET  /files/{sha}   -> one of them, as kept
// TH_NATIVE_URLS (default http://127.0.0.1:7300) sets where it listens.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("TH_NATIVE_URLS") ?? "http://127.0.0.1:7300");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

var files = new ConcurrentDictionary<string, (string name, byte[] content)>();
var served = new ConcurrentDictionary<string, int>();
INativeActivity[] activities = [new ValidateCbxFile(), new RenderCbSif((sha, name, content) => files[sha] = (name, content))];
var byName = activities.ToDictionary(x => x.Name);

app.MapGet("/health", () => Results.Ok(new { activities = byName.Keys.Order(), served }));

app.MapPost("/native", async (HttpRequest req) =>
{
    var body = await JsonNode.ParseAsync(req.Body) as JsonObject ?? throw new BadHttpRequestException("not JSON");
    var name = (string?)body["activity"] ?? "";
    if (!byName.TryGetValue(name, out var act))
        return Results.Ok(new { kind = "error", name = "NotNative", retryable = false, message = $"The native host serves no {name}" });
    var now = DateTimeOffset.TryParse((string?)body["now"], out var t) ? t : DateTimeOffset.UtcNow;
    try
    {
        var r = act.Run(new NativeAttempt(name, (string?)body["release"], body["view"] as JsonObject ?? new JsonObject(), body["params"] as JsonObject ?? new JsonObject(), now));
        served.AddOrUpdate(name, 1, (_, n) => n + 1);
        return Results.Ok(new JsonObject { ["kind"] = "result", ["outcome"] = r.Outcome, ["reasonCode"] = r.ReasonCode, ["writes"] = r.Writes });
    }
    catch (Exception e)
    {
        return Results.Ok(new { kind = "error", name = "NativeFailure", retryable = false, message = e.Message });
    }
});

app.MapGet("/files", () => Results.Ok(files.Select(f => new { sha256 = f.Key, name = f.Value.name, bytes = f.Value.content.Length })));

app.MapGet("/files/{sha}", (string sha) => files.TryGetValue(sha, out var f)
    ? Results.File(f.content, "text/plain", f.name)
    : Results.NotFound());

app.Run();

