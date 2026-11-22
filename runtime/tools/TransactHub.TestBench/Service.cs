using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.FileProviders;

namespace TransactHub.TestBench;

/// <summary>A run or a suite the service holds: its progress as it goes, and its result when done.</summary>
public sealed class Job
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Actor { get; init; }
    public required JsonObject Request { get; init; }
    public string Status { get; set; } = "queued";
    public ConcurrentQueue<string> Progress { get; } = new();
    public JsonObject? Result { get; set; }
    public DateTimeOffset Queued { get; } = DateTimeOffset.UtcNow;

    public JsonObject Json(bool full = true)
    {
        var o = new JsonObject { ["id"] = Id, ["kind"] = Kind, ["status"] = Status, ["actor"] = Actor, ["queued"] = Queued.ToString("O"), ["progress"] = new JsonArray(Progress.Select(p => (JsonNode?)p).ToArray()) };
        if (Result is not null) { o["passed"] = Result["passed"]?.DeepClone(); if (full) o["result"] = Result.DeepClone(); }
        return o;
    }
}

/// <summary>
/// The test bench service: a small host of its own (never the production API), with the bank's sign-in, a queue that runs
/// one job at a time on infrastructure kept for its life, the run history on disk, and the web UI (runtime/testbench/web).
/// </summary>
public static class Service
{
    public static async Task<int> Serve(Home home, string[] args)
    {
        var urls = args.SkipWhile(a => a != "--urls").Skip(1).FirstOrDefault() ?? Environment.GetEnvironmentVariable("TH_TESTBENCH_URLS") ?? "http://127.0.0.1:4380";
        var app = Build(home, urls, null);
        await app.RunAsync();
        return 0;
    }

    public interface IJobRunner
    {
        Task<JsonObject> Run(Plan plan, string id, Action<string> progress, CancellationToken ct);
        Task<JsonObject> Suite(string dir, JsonObject? overrides, Action<string> progress, CancellationToken ct);
    }

    sealed class RealRunner(Home home, Infra infra) : IJobRunner
    {
        public Task<JsonObject> Run(Plan plan, string id, Action<string> progress, CancellationToken ct) => new Runner(home, infra).Run(plan, id, progress, ct);
        public Task<JsonObject> Suite(string dir, JsonObject? overrides, Action<string> progress, CancellationToken ct) => Cases.Run(home, infra, dir, overrides, progress, ct);
    }

    static ClaimsPrincipal User(HttpContext c) => c.User;

    public static WebApplication Build(Home home, string urls, Func<IJobRunner>? runnerFactory, string[]? args = null)
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args ?? [], ContentRootPath = home.Bench });
        b.WebHost.UseUrls(urls);
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        var baseUrl = urls.Split(';')[0];
        var auth = OidcBenchAuth.Add(b, home, baseUrl);
        var app = b.Build();
        var infra = new Infra(home, null);
        var runner = runnerFactory?.Invoke() ?? new RealRunner(home, infra);
        var catalogue = new Catalogue(home);
        var data = new TestData(home);
        var jobs = new ConcurrentDictionary<string, Job>();
        var queue = Channel.CreateUnbounded<Job>();
        var stop = app.Lifetime.ApplicationStopping;
        app.Lifetime.ApplicationStopped.Register(() => infra.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(30)));

        // One job at a time: the engines, the containers and the ports of a run are not shared between runs.
        _ = Task.Run(async () =>
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stop))
            {
                job.Status = "running";
                void say(string s) => job.Progress.Enqueue(s);
                try
                {
                    if (job.Kind == "run")
                    {
                        var plan = catalogue.Resolve(job.Request);
                        job.Result = await runner.Run(plan, job.Id, say, stop);
                    }
                    else
                    {
                        var dir = J.S(job.Request, "suite") == "saved" ? home.SavedCases : home.Suite;
                        var ov = new JsonObject();
                        foreach (var k in new[] { "engines", "databases" }) if (job.Request[k] is { } v) ov[k] = v.DeepClone();
                        job.Result = await runner.Suite(dir, ov.Count > 0 ? ov : null, say, stop);
                    }
                    job.Status = "done";
                }
                catch (Exception e) when (e is BenchError or OperationCanceledException)
                {
                    job.Result = new JsonObject { ["passed"] = false, ["failures"] = new JsonArray(e.Message) };
                    job.Status = "done";
                }
                catch (Exception e)
                {
                    job.Result = new JsonObject { ["passed"] = false, ["failures"] = new JsonArray($"The run failed: {e.Message}") };
                    job.Status = "done";
                }
            }
        }, stop);

        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (BenchError e) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
            catch (Forbidden e) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
        });
        auth.Map(app);

        app.MapGet("/api/health", () => Results.Json(new JsonObject { ["ok"] = true, ["auth"] = auth.Mode })).AllowAnonymous();
        app.MapGet("/auth/config", () => Results.Json(auth.ClientConfig())).AllowAnonymous();

        var api = app.MapGroup("/api").RequireAuthorization(BenchRoles.Policy);
        api.MapGet("/me", (HttpContext c) => Results.Json(new JsonObject
        {
            ["name"] = BenchRoles.Actor(c.User),
            ["roles"] = new JsonArray(c.User.FindAll(BenchRoles.RoleClaim).Select(x => (JsonNode?)x.Value).ToArray()),
            ["tenants"] = new JsonArray(c.User.FindAll(BenchRoles.TenantClaim).Select(x => (JsonNode?)x.Value).ToArray()),
        }));
        api.MapGet("/profiles", () => Results.Json(new JsonArray(catalogue.Profiles().Select(p =>
        {
            p["benchPorts"] = J.S(p, "bench") is { } bn ? new JsonArray(catalogue.Bench(bn).Select(x => (JsonNode?)x.ToString()).ToArray()) : null;
            return (JsonNode?)p;
        }).ToArray())));
        api.MapGet("/benches", () =>
        {
            var o = new JsonObject();
            foreach (var kv in catalogue.Benches()) o[kv.Key] = new JsonObject { ["title"] = J.S(kv.Value, "title"), ["ports"] = new JsonArray(catalogue.Bench(kv.Key).Select(x => (JsonNode?)x.ToString()).ToArray()) };
            return Results.Json(o);
        });

        // Test data: the library, the builder, validation and saving (always synthetic).
        api.MapGet("/data/library", (string? scheme) => Results.Json(new JsonArray(data.List(scheme).Select(e => (JsonNode?)e).ToArray())));
        api.MapGet("/data/library/{id}", (string id, string? tenant) =>
        {
            var e = new Library(home).Entry(id);
            var (text, name) = new Library(home).Payload(id, tenant ?? "example-bank");
            e["text"] = text.Length > 200_000 ? text[..200_000] : text;
            e["name"] = name;
            e["truncated"] = text.Length > 200_000;
            return Results.Json(e);
        });
        api.MapGet("/data/templates", () =>
        {
            var builder = new DataBuilder(home);
            return Results.Json(new JsonObject
            {
                ["templates"] = new JsonArray(DataBuilder.Templates.Select(t => (JsonNode?)t).ToArray()),
                ["injections"] = new JsonObject(DataBuilder.Injections.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
                ["tenants"] = new JsonArray(builder.Tenants().Select(t => (JsonNode?)new JsonObject { ["tenant"] = t, ["routingCode"] = builder.RoutingCode(t) }).ToArray()),
            });
        });
        api.MapPost("/data/build", (JsonObject recipe) =>
        {
            var built = data.Build(recipe);
            var v = data.Validate(Inputs.Detect(built.Text, built.Name), J.S(recipe, "tenant") ?? "example-bank");
            return Results.Json(new JsonObject { ["text"] = built.Text, ["name"] = built.Name, ["template"] = built.Template, ["injected"] = new JsonArray(built.Injected.Select(i => (JsonNode?)i).ToArray()), ["suggestedBench"] = built.SuggestedBench, ["validation"] = v.Json() });
        });
        api.MapPost("/data/validate", async (JsonObject body, HttpContext c) =>
        {
            var tenant = J.S(body, "tenant") ?? "example-bank";
            Tenant(c, tenant);
            var d = body["data"] as JsonObject ?? throw new BenchError("validate takes { data, tenant }");
            var input = Inputs.Read(d, new Library(home), tenant);
            var v = data.Validate(input, tenant).Json();
            v["input"] = input.Summary();
            if (body["rules"]?.GetValue<bool>() == true) v["rules"] = await data.Rules(infra, d, J.S(body, "profile") ?? "wps-reference", tenant, J.S(body, "bench"));
            return Results.Json(v);
        });
        api.MapPost("/data/samples", (JsonObject body) =>
        {
            var file = data.Save(J.S(body, "id") ?? throw new BenchError("A sample needs an id"), J.S(body, "text") ?? throw new BenchError("A sample needs its text"), J.S(body, "name") ?? "sample", body["tags"] as JsonObject ?? new JsonObject());
            return Results.Json(new JsonObject { ["saved"] = Path.GetRelativePath(home.Repo, file) });
        });

        // Runs: POST {profile, data, …} queues one; GET follows it. ?wait=true answers when it is done.
        api.MapPost("/runs", async (JsonObject request, HttpContext c, bool? wait) =>
        {
            var plan = catalogue.Resolve(request);
            Tenant(c, plan.Tenant);
            var job = new Job { Id = Runner.NewId(), Kind = "run", Actor = BenchRoles.Actor(c.User), Request = request };
            jobs[job.Id] = job;
            await queue.Writer.WriteAsync(job);
            if (wait == true) { while (job.Status != "done") await Task.Delay(250, c.RequestAborted); return Results.Json(job.Json()); }
            return Results.Json(job.Json(false), statusCode: 202);
        });
        api.MapGet("/runs", () =>
        {
            var list = new JsonArray();
            foreach (var j in jobs.Values.Where(j => j.Kind == "run" && j.Status != "done").OrderByDescending(j => j.Queued)) list.Add(new JsonObject { ["id"] = j.Id, ["status"] = j.Status, ["actor"] = j.Actor, ["profile"] = J.S(j.Request, "profile") });
            if (Directory.Exists(home.Runs))
                foreach (var d in Directory.EnumerateDirectories(home.Runs).OrderDescending(StringComparer.Ordinal).Take(200))
                {
                    var f = Path.Combine(d, "result.json");
                    if (!File.Exists(f)) continue;
                    try
                    {
                        var r = JsonNode.Parse(File.ReadAllText(f))!;
                        list.Add(new JsonObject { ["id"] = J.S(r, "id"), ["status"] = "done", ["actor"] = jobs.TryGetValue(J.S(r, "id") ?? "", out var jj) ? jj.Actor : null, ["profile"] = J.S(r["plan"], "profile"), ["input"] = J.S(r["plan"]?["input"], "name"), ["passed"] = r["passed"]?.DeepClone(), ["startedAt"] = J.S(r, "startedAt") });
                    }
                    catch (System.Text.Json.JsonException) { }
                }
            return Results.Json(list);
        });
        api.MapGet("/runs/{id}", (string id) =>
        {
            if (jobs.TryGetValue(id, out var j)) return Results.Json(j.Json());
            var f = SafeRun(home, id, "result.json");
            return f is null ? Results.NotFound() : Results.Json(new JsonObject { ["id"] = id, ["status"] = "done", ["result"] = JsonNode.Parse(File.ReadAllText(f)) });
        });
        api.MapGet("/runs/{id}/report.{ext}", (string id, string ext) =>
        {
            if (ext is not ("html" or "md")) return Results.NotFound();
            var f = SafeRun(home, id, $"report.{ext}");
            return f is null ? Results.NotFound() : Results.Content(File.ReadAllText(f), ext == "html" ? "text/html" : "text/markdown");
        });
        api.MapPost("/runs/{id}/case", (string id, JsonObject body) =>
        {
            var f = SafeRun(home, id, "result.json") ?? throw new BenchError($"No finished run {id}");
            var c = Cases.FromRun((JsonObject)JsonNode.Parse(File.ReadAllText(f))!, J.S(body, "id") ?? throw new BenchError("A case needs an id"), J.S(body, "title"));
            var file = Cases.Save(home, c);
            return Results.Json(new JsonObject { ["saved"] = Path.GetRelativePath(home.Repo, file), ["case"] = c });
        });
        api.MapGet("/cases", () =>
        {
            var list = new JsonArray();
            foreach (var (which, dir) in new[] { ("shipped", home.Suite), ("saved", home.SavedCases) })
                if (Directory.Exists(dir))
                    foreach (var (file, c) in Cases.Load(dir))
                        list.Add(new JsonObject { ["suite"] = which, ["id"] = J.S(c, "id"), ["title"] = J.S(c, "title"), ["profile"] = J.S(c["request"], "profile"), ["expect"] = J.S(c["expect"], "outcome"), ["file"] = Path.GetRelativePath(home.Repo, file) });
            return Results.Json(list);
        });
        api.MapPost("/suites", async (JsonObject request, HttpContext c) =>
        {
            var job = new Job { Id = Runner.NewId() + "-suite", Kind = "suite", Actor = BenchRoles.Actor(c.User), Request = request };
            jobs[job.Id] = job;
            await queue.Writer.WriteAsync(job);
            return Results.Json(job.Json(false), statusCode: 202);
        });
        api.MapGet("/suites/{id}", (string id) => jobs.TryGetValue(id, out var j) && j.Kind == "suite" ? Results.Json(j.Json()) : Results.NotFound());

        // The web UI, built into runtime/testbench/web/dist.
        var dist = Path.Combine(home.Bench, "web", "dist");
        if (Directory.Exists(dist))
        {
            var files = new PhysicalFileProvider(dist);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
            app.MapFallback(async ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = 404; return; }
                ctx.Response.ContentType = "text/html";
                await ctx.Response.SendFileAsync(Path.Combine(dist, "index.html"));
            }).AllowAnonymous();
        }
        return app;
    }

    static void Tenant(HttpContext c, string tenant)
    {
        if (!BenchRoles.MayActFor(c.User, tenant)) throw new Forbidden($"{BenchRoles.Actor(c.User)} may not run tests for the tenant {tenant}");
    }

    public sealed class Forbidden(string m) : Exception(m);

    /// <summary>A file of a finished run, by an id that cannot leave the runs folder.</summary>
    static string? SafeRun(Home home, string id, string file)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z0-9-]{1,80}$")) return null;
        var f = Path.Combine(home.Runs, id, file);
        return File.Exists(f) ? f : null;
    }
}
