using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using TransactHub.Api;
using TransactHub.Engine.Flowable;
using TransactHub.Operations;
using TransactHub.Security;
using TransactHub.Store;
using TransactHub.Telemetry;

namespace TransactHub.Service.Api;

/// <summary>
/// The service console's API (spec R-06-25, R-09-39 to R-09-44), routes under <c>/service/api</c>, and the runtime's web app
/// (runtime/ops-console/dist), whose Service section it serves. Configuration:
/// <list type="bullet">
/// <item><c>Store:Provider</c>, <c>Store:Connection</c>, <c>Operations:Connection</c>: the store and the read model.</item>
/// <item><c>TH_TRUSTED_SIGNERS</c> (or <c>Signing:TrustedSigners</c>): the signers whose packages this runtime accepts.</item>
/// <item><c>PackageSource:GitHub:Repository|Api|Root|TokenRef</c>: GitHub as the artefact repository, read only.</item>
/// <item><c>Service:Environment</c> (default production), <c>Releases:TestEnvironments</c> (test,dev,kit).</item>
/// <item><c>Service:Errors</c> (default content-configurations/artefacts), <c>Service:Docs</c> (default docs/north-star), <c>Service:Console</c>.</item>
/// <item><c>Service:Probes:n:*</c>, <c>Service:Certificates:n:Name|Path</c>, <c>Service:Disks:n</c>, <c>Service:Dashboards:*</c>, <c>Service:Banks</c>: health.</item>
/// <item><c>TH_BACKUP_SIGNING_KEY</c> (a reference), <c>Service:Backup:TrustedSigners</c>, <c>Service:RestoreDir</c>: backups.</item>
/// <item><c>TH_FLOWABLE_URL</c>, <c>TH_FLOWABLE_USER</c>, <c>Service:Flowable:PasswordRef</c>: the Flowable engine; <c>Service:TrustWritable</c>.</item>
/// <item><c>TH_ASSISTANT_ENABLED</c> (true to switch on), <c>TH_ANTHROPIC_API_KEY</c>: the assistant.</item>
/// <item><c>Auth:*</c> as every runtime host (TransactHub.Auth), <c>TH_AUTH_DEV=1</c> outside production only.</item>
/// </list>
/// </summary>
public sealed class ServiceHost
{
    public static async Task Main(string[] args)
    {
        var app = await Build(args);
        await app.RunAsync();
    }

    static string? Find(string rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, rel);
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    public static async Task<WebApplication> Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var cfg = builder.Configuration;
        var provider = Enum.Parse<StoreProvider>(cfg["Store:Provider"] ?? "SqlServer", ignoreCase: true);
        var store = new StoreOptions(provider, cfg["Store:Connection"] ?? throw new InvalidOperationException("Set Store:Connection"));
        var ops = new StoreOptions(provider, cfg["Operations:Connection"] ?? store.ConnectionString);
        var secrets = SecretResolver.FromEnvironment(k => cfg[k]);
        var trust = cfg["Signing:TrustedSigners"] ?? cfg["TH_TRUSTED_SIGNERS"] ?? throw new InvalidOperationException("Set TH_TRUSTED_SIGNERS: the signers whose packages this runtime accepts (spec R-09-25)");
        _ = await TrustedSigners.Load(trust, secrets);
        var clock = TimeProvider.System;
        var auth = await builder.AddServiceAuthentication();
        var testEnvs = (cfg["Releases:TestEnvironments"] ?? "test,dev,kit").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var environment = cfg["Service:Environment"] ?? "production";
        var errorsDir = cfg["Service:Errors"] ?? Find("content-configurations/artefacts");
        var docsDir = cfg["Service:Docs"] ?? Find("docs/north-star");
        var viewsDir = cfg["Operations:Views"] ?? errorsDir;
        var logBuffer = new LogBuffer();
        builder.Logging.AddProvider(logBuffer);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);

        var s = builder.Services;
        s.AddSingleton(logBuffer);
        s.AddSingleton<TimeProvider>(clock);
        s.AddSingleton(new ContentSettings(trust, secrets, testEnvs, environment));
        s.AddSingleton(sp => ErrorDictionary.FromFolder(errorsDir));
        s.AddSingleton(sp => ViewCatalog.FromFolder(viewsDir ?? "content-configurations/artefacts"));
        s.AddSingleton(sp => new Projector(store, ops, sp.GetRequiredService<ViewCatalog>(), clock));
        s.AddSingleton<IEngineSignals>(new OutboxSignals(store, clock));
        if (GitHubPackageSource.FromConfiguration(cfg, secrets) is { } src) s.AddSingleton<IPackageSource>(src);
        s.AddSingleton(new ServiceAudit(store, clock));
        s.AddSingleton(sp => new ContentService(store, ops, sp.GetService<IPackageSource>(), sp.GetRequiredService<ContentSettings>(), sp.GetRequiredService<ServiceAudit>(), sp.GetRequiredService<ErrorDictionary>(), clock));
        s.AddSingleton(sp => new BackupService(store, sp.GetRequiredService<ContentService>(), sp.GetRequiredService<ContentSettings>(),
            new BackupSettings(cfg["Service:Installation"] ?? environment, cfg["TH_BACKUP_SIGNING_KEY"], cfg["Service:Backup:TrustedSigners"] ?? trust, cfg["Service:Banks"] ?? cfg["TH_BANKS_DIR"], cfg["Auth:RoleMap"], cfg["Service:RestoreDir"]),
            secrets, sp.GetRequiredService<ServiceAudit>(), clock));
        s.AddSingleton(sp => new EventLog(store, ops, logBuffer, sp.GetRequiredService<ErrorDictionary>()));
        s.AddSingleton<HttpClient>();
        s.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<HttpClient>();
            var dash = (string name) => cfg[$"Service:Dashboards:{name}"];
            var probes = new List<IProbe> { Probes.Store(store, dash("Store")), Probes.ReadModel(sp.GetRequiredService<Projector>(), dash("Store")) };
            if (sp.GetService<IPackageSource>() is { } ps) probes.Add(Probes.Source(ps));
            probes.Add(Probes.Signers(trust, secrets, clock, TimeSpan.FromDays(int.TryParse(cfg["Service:Health:WarnDays"], out var wd) ? wd : 30)));
            if (FlowableRest.FromEnvironment() is { } f) probes.Add(Probes.Flowable(f, dash("Engines")));
            if (cfg["TH_TEMPORAL_ADDRESS"] is { Length: > 0 } t) probes.Add(Probes.Tcp("temporal", "engines", t, dash("Engines"), "temporal.ServerUnreachable"));
            foreach (var c in cfg.GetSection("Service:Certificates").GetChildren())
                if (c["Path"] is { } path) probes.Add(Probes.Certificate(c["Name"] ?? c.Key, path, clock, TimeSpan.FromDays(30)));
            var disks = cfg.GetSection("Service:Disks").GetChildren().Select(c => c.Value).OfType<string>().ToList();
            foreach (var d in disks.Count > 0 ? disks : [AppContext.BaseDirectory]) probes.Add(Probes.Disk(d));
            probes.AddRange(Probes.Configured(cfg, http));
            return new HealthMonitor(probes, clock);
        });
        s.AddHostedService<HealthSampler>();
        s.AddSingleton(_ => new AssistantModel(AnthropicAssistantModel.FromEnvironment(k => cfg[k])));
        s.AddSingleton(sp => new AssistantService(sp.GetRequiredService<AssistantModel>().Model, cfg["TH_ASSISTANT_ENABLED"] == "true", store, sp.GetRequiredService<HealthMonitor>(),
            sp.GetRequiredService<EventLog>(), sp.GetRequiredService<ContentService>(), sp.GetRequiredService<ErrorDictionary>(), docsDir));
        s.AddSingleton(sp =>
        {
            var book = new ActionBook(store, sp.GetRequiredService<ServiceAudit>());
            var content = sp.GetRequiredService<ContentService>();
            var projector = sp.GetRequiredService<Projector>();
            book.Run("reverify-package", (who, a, ct) => content.Reverify(a.Target!, who, ct));
            book.Run("activate", (who, a, ct) => content.Activate(a.Target!, (string?)a.Parameters?["environment"],
                (string?)a.Parameters?["effectiveFrom"] is { Length: > 0 } f ? DateTimeOffset.Parse(f, System.Globalization.CultureInfo.InvariantCulture) : null, who, a.Reason!, ct));
            book.Run("rollback", (who, a, ct) => content.Rollback(a.Target!, (string?)a.Parameters?["environment"], who, a.Reason!, ct));
            book.Run("remove", (who, a, ct) => content.Remove(a.Target!, who, a.Reason!, ct, (string?)a.Parameters?["environment"]));
            book.Run("drain", (who, a, ct) => content.Drain(a.Target!, (string?)a.Parameters?["environment"], who, a.Reason!, ct));
            book.Run("rebuild-read-model", async (_, _, ct) => { await projector.Rebuild(ct); var l = await projector.Lag(ct); return new JsonObject { ["rebuilt"] = true, ["behind"] = l.Behind, ["applied"] = l.Applied }; });
            book.Run("replay-read-model", async (_, _, ct) => { var before = await projector.Lag(ct); await projector.CatchUp(ct); var l = await projector.Lag(ct); return new JsonObject { ["from"] = before.Through, ["through"] = l.Through, ["behind"] = l.Behind }; });
            book.Run("release-hold", ActionBook.ReleaseHold(store, clock, sp.GetRequiredService<IEngineSignals>()));
            var signals = sp.GetRequiredService<IEngineSignals>();
            book.Run("re-screen", ActionBook.ReScreen(store, signals));
            // The operator's controls of a parked step, through the engine's host (R-05-55, R-05-57).
            book.Run("retry-attempt", ActionBook.Signal(store, signals, "retry", needsParked: true));
            book.Run("redrive-workflow", ActionBook.Signal(store, signals, "redrive", needsParked: true));
            book.Run("reset-activity", ActionBook.Signal(store, signals, "redrive", needsParked: true, new JsonObject { ["reset"] = true }));
            book.Run("expire-hold", ActionBook.ExpireHold(store, new HoldExpiry(store, signals, clock)));
            // The file-transfer port the runtime resends on (R-05-58): a folder the scheme's gateway collects from.
            if ((cfg["Service:FileTransfer:Folder"] ?? cfg["TH_FILE_TRANSFER_DIR"]) is { Length: > 0 } outFolder)
                book.Run("resend-file", ActionBook.ResendFile(store, new FileResend(store, new FolderFileTransfer(outFolder), clock)));
            // Core banking, as the banks' port bindings bind it (R-08-17): what reconcile-posting asks.
            var banks = cfg["Service:Banks"] ?? cfg["TH_BANKS_DIR"];
            var resolver = new TransactHub.Activities.Platform.Posting.CoreBankingResolver(banks is { Length: > 0 } ? TransactHub.Activities.Platform.Posting.CoreBankingResolver.ReadBanks(banks) : new TransactHub.Ports.PortBindings(), secrets: secrets);
            book.Run("reconcile-posting", ActionBook.ReconcilePosting(store, signals, tenant => { try { return resolver.For(tenant, clock.GetUtcNow()).CoreBanking; } catch (Exception) { return null; } }, clock));
            book.Run("pause-intake", ActionBook.Intake(store, true, clock));
            book.Run("resume-intake", ActionBook.Intake(store, false, clock));
            book.Run("drain-worker", ActionBook.Worker("drain"));
            book.Run("restart-worker", ActionBook.Worker("restart"));
            book.Run("rotate-trusted-key", ActionBook.RotateKey(trust, secrets, cfg["Service:TrustWritable"] == "true", clock));
            if ((cfg["Service:Flowable:Url"] ?? cfg["TH_FLOWABLE_URL"]) is { Length: > 0 } fu)
                book.Run("requeue-dead-letter", ActionBook.RequeueDeadLetter(fu, cfg["TH_FLOWABLE_USER"] ?? "rest-admin", cfg["Service:Flowable:PasswordRef"] ?? "env:TH_FLOWABLE_PASSWORD", secrets,
                    sp.GetService<FlowableHandler>()?.Handler));
            return book;
        });
        if (cfg["Service:Project"] == "true") s.AddHostedService<ProjectionLoop>();
        s.AddTransactHubTelemetry("transacthub-service");
        s.AddHealthChecks().AddCheck<StoreReady>("store", tags: [WebTelemetry.Ready]);
        s.AddScoped(_ => StoreDbContext.Open(store));

        var app = builder.Build();
        if (cfg["Store:Migrate"] != "false")
        {
            await using (var db = StoreDbContext.Open(store)) await db.Database.MigrateAsync();
            await app.Services.GetRequiredService<Projector>().Migrate();
        }
        await app.Services.GetRequiredService<ErrorDictionary>().AddStored(store);
        app.MapTransactHubHealth();
        var consoleDir = cfg["Service:Console"] ?? cfg["Operations:Console"] ?? Find("runtime/ops-console/dist");
        var files = consoleDir is not null && Directory.Exists(consoleDir) ? new PhysicalFileProvider(Path.GetFullPath(consoleDir)) : null;
        if (files is not null)
        {
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }
        app.UseAuthentication();
        app.UseAuthorization();
        ServiceEndpoints.Map(app, auth);
        if (files is not null)
            app.MapFallback(async ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/service/api") || ctx.Request.Path.StartsWithSegments("/ops/api")) { ctx.Response.StatusCode = 404; return; }
                ctx.Response.ContentType = "text/html";
                await ctx.Response.SendFileAsync(files.GetFileInfo("index.html"));
            }).AllowAnonymous();
        return app;
    }
}

/// <summary>The assistant's model: the Anthropic API when a key is set; a test gives its own.</summary>
public sealed record AssistantModel(IAssistantModel? Model);

/// <summary>A test's or an installation's own handler for the Flowable REST server (the default is a plain client).</summary>
public sealed record FlowableHandler(HttpMessageHandler Handler);

/// <summary>Keeps the read model up with the outbox when this host is the installation's projecting process (Service:Project=true).</summary>
sealed class ProjectionLoop(Projector p, IConfiguration cfg, ILogger<ProjectionLoop> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var every = TimeSpan.FromMilliseconds(int.TryParse(cfg["Operations:PollMilliseconds"], out var ms) ? ms : 500);
        while (!ct.IsCancellationRequested)
        {
            try { await p.CatchUp(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { log.LogWarning("projection: {Error} (readmodel.Gap or store.Unavailable)", e.Message); }
            try { await Task.Delay(every, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
