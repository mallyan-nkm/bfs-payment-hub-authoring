using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TransactHub.Security.Tests;

/// <summary>
/// The laptop deployment (deploy/laptop, spec R-09-80), read as files: every image pinned by digest or built here (th-laptop-*),
/// every final image an unprivileged user with a health check, no secret value in the compose file or the images (a password or
/// a client secret only from the environment, a key only from a volume), every port on 127.0.0.1, and the development identity
/// provider refused in Production. The stack itself is brought up by deploy/laptop/test/insert-only.mts.
/// </summary>
public sealed class LaptopDeploymentTests
{
    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "deploy", "laptop")) && Directory.Exists(Path.Combine(d.FullName, "runtime"))) return d.FullName;
        throw new DirectoryNotFoundException("the repository with deploy/laptop");
    }

    static readonly string Laptop = Path.Combine(Root(), "deploy", "laptop");
    static string Compose => File.ReadAllText(Path.Combine(Laptop, "compose.yaml"));
    static string Dockerfile => File.ReadAllText(Path.Combine(Laptop, "Dockerfile"));

    [Fact]
    public void Every_image_is_pinned_by_digest_or_built_here()
    {
        var images = Regex.Matches(Compose, @"^\s+image:\s*(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(images);
        Assert.All(images, i => Assert.True(i.Contains("@sha256:") || i.StartsWith("th-laptop-"), $"{i} is neither pinned by digest nor one of the laptop's own images"));
        var bases = Regex.Matches(Dockerfile, @"^ARG \w+_IMAGE=(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(4, bases.Count);
        Assert.All(bases, b => Assert.Matches(@"@sha256:[0-9a-f]{64}$", b));
    }

    /// <summary>The .NET projects a build publishes: the words src/... and tools/... in its publish loop.</summary>
    static string[] Published(string text) =>
        Regex.Matches(text, @"(?<![\w/])((?:src|tools)/TransactHub\.[\w.]+)").Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

    [Fact]
    public void The_local_build_publishes_every_project_the_source_build_does()
    {
        // build.sh --local feeds the same final stages as the Dockerfile's source build; a project missing from it leaves its
        // service with no program in the image (the File API's container restarted for want of its dll).
        var source = Dockerfile[Dockerfile.IndexOf("AS dotnet-source\n", StringComparison.Ordinal)..Dockerfile.IndexOf("AS ops-console-source", StringComparison.Ordinal)];
        var script = File.ReadAllText(Path.Combine(Laptop, "build.sh"));
        var local = script[script.IndexOf("for p in", StringComparison.Ordinal)..script.IndexOf("done", script.IndexOf("for p in", StringComparison.Ordinal), StringComparison.Ordinal)];
        Assert.Contains("src/TransactHub.FileApi", Published(source));
        Assert.Equal(Published(source), Published(local));
    }

    [Fact]
    public void Every_final_image_runs_as_an_unprivileged_user_with_a_health_check()
    {
        foreach (var target in new[] { "studio", "runtime", "temporal" })
        {
            var start = Dockerfile.IndexOf($" AS {target}\n", StringComparison.Ordinal);
            Assert.True(start > 0, $"no target {target}");
            var next = Dockerfile.IndexOf("\nFROM ", start, StringComparison.Ordinal);
            var stage = next < 0 ? Dockerfile[start..] : Dockerfile[start..next];
            Assert.Contains("USER app", stage);
            Assert.Contains("HEALTHCHECK", stage);
            Assert.DoesNotContain("USER root", stage);
        }
    }

    [Fact]
    public void No_secret_value_is_in_the_compose_file_or_an_image_and_every_port_is_on_loopback()
    {
        foreach (Match m in Regex.Matches(Compose, @"^\s+([A-Za-z0-9_]*(PASSWORD|SECRET|TOKEN)[A-Za-z0-9_]*):\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (m.Groups[1].Value.EndsWith("_ROOTS") || m.Groups[1].Value.EndsWith("_URL")) continue; // where secrets are read from, or a token endpoint: not a secret
            var v = m.Groups[3].Value.Trim().Trim('"');
            Assert.True(v.Length == 0 || v.StartsWith("${") || v.StartsWith("env:") || v.StartsWith("file:"), $"{m.Groups[1].Value} holds a value, not a reference or the environment: {v}");
        }
        foreach (Match m in Regex.Matches(Compose, @"Password=([^;""]*)"))
            Assert.StartsWith("${", m.Groups[1].Value);
        foreach (Match m in Regex.Matches(Compose, @"ports:\s*\[(.*?)\]"))
            Assert.All(m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries), p => Assert.StartsWith("\"127.0.0.1:", p.Trim()));
        var ignore = File.ReadAllText(Path.Combine(Laptop, "Dockerfile.dockerignore"));
        foreach (var never in new[] { "**/.env", "**/*.pem", "**/*.key", ".git" }) Assert.Contains(never, ignore.Split('\n').Select(l => l.Trim()));
        Assert.DoesNotMatch(@"(?im)^\s*(ENV|ARG)\s+\S*(PASSWORD|SECRET|TOKEN)\S*=", Dockerfile);
    }

    [Fact]
    public void The_engine_host_answers_the_File_API_on_the_compose_network_with_its_token_only()
    {
        // R-09-95 (I-158 (3)): every engine host supervisor listens beyond loopback, inside the compose network (no published port),
        // and takes the token by reference; the File API's front door calls it with the same token.
        var hosts = Regex.Matches(Compose, @"^  (enginehost[a-z-]*):(?: &\S+)?\n((?:    .*\n|\n)*?)(?=^  \S|\z)", RegexOptions.Multiline).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        Assert.Equal(4, hosts.Count);
        foreach (var (name, body) in hosts)
        {
            Assert.Contains("TH_HOST_URLS: http://0.0.0.0:7600/", body);
            Assert.Contains("TH_ENGINE_HOST_TOKEN: env:TH_LAPTOP_ENGINE_HOST_TOKEN", body);
            Assert.DoesNotContain("ports:", body);
        }
        var fileApi = Regex.Match(Compose, @"^  file-api:\n((?:    .*\n|\n)*?)(?=^  \S)", RegexOptions.Multiline).Groups[1].Value;
        Assert.Contains("FileApi__EngineHost: http://enginehost:7600", fileApi);
        Assert.Contains("FileApi__EngineHostTokenRef: env:TH_LAPTOP_ENGINE_HOST_TOKEN", fileApi);
    }

    static string Service(string name) => Regex.Match(Compose, $@"^  {Regex.Escape(name)}:(?: &\S+)?\n((?:    .*\n|\n)*?)(?=^  \S|^\S|\z)", RegexOptions.Multiline).Groups[1].Value;

    [Fact]
    public void Every_file_enters_and_leaves_through_the_File_API_on_the_laptop()
    {
        // R-09-105: the bank's partners are the stack's SFTP server, with its login from the environment and its host key pinned by
        // reference; the File API polls its inboxes and runs the intake maps on the workers of the payment's distribution; every
        // host that puts a file names the File API; the consoles resend through it; the engine hosts are served by the workers.
        var sftp = Service("sftp");
        Assert.Contains("atmoz/sftp:alpine@sha256:", sftp);
        Assert.Contains("${TH_LAPTOP_SFTP_PASSWORD:?", sftp);
        Assert.Contains("x-sftp: &sftp-refs\n  TH_LAPTOP_SFTP_LOGIN:", Compose);
        // No host reads a folder of bindings (R-09-113): the laptop's binding arrives in the bank's configuration release.
        Assert.DoesNotContain("TH_BANKS_DIR", Compose);
        Assert.Matches(@"x-file-api: &file-api-client\n  TH_FILE_API_URL: http://file-api:8080\n  TH_FILE_API_TOKEN_REF: file:", Compose);
        var fileApi = Service("file-api");
        Assert.Contains("<<: [*store, *auth, *sftp-refs]", fileApi);
        Assert.Contains("FileApi__Workers__uae.wps: http://workers:7700/{distribution}/_/any", fileApi);
        Assert.Contains("*file-api-client", Service("native"));
        Assert.Contains("*sftp-refs", Service("native"));
        foreach (var host in new[] { "enginehost", "enginehost-dtfx", "enginehost-elsa", "enginehost-flowable" })
        {
            Assert.Contains("<<: *file-api-client", Service(host));
            Assert.Contains("TH_HOST_WORKERS_URL: http://workers:7700/{distribution}/{workflow}/{queue}", Service(host));
        }
        Assert.Contains("Operations__FileApi__Url: http://file-api:8080", Service("ops-api"));
        Assert.Contains("Operations__FileApi__TokenRef: file:", Service("ops-api"));
        Assert.Contains("Service__FileApi__Url: http://file-api:8080", Service("service-api"));
        Assert.Contains("command: [\"workers\"]", Service("workers"));
        Assert.Contains("command: [\"tokens\"]", Service("machine-auth"));
        // The laptop's own binding of each bank: every inbox names its message (I-160), the host key and the login by reference (I-161).
        foreach (var bank in new[] { "adib", "dib" })
        {
            var b = File.ReadAllText(Path.Combine(Laptop, "banks", bank, "file-transfer.yaml"));
            Assert.Contains($"tenant: {bank}", b);
            Assert.Contains("hostKeyRef: env:TH_LAPTOP_SFTP_HOST_KEY", b);
            Assert.Contains("secretRef: env:TH_LAPTOP_SFTP_LOGIN", b);
            Assert.Contains("pickup.message: uae.wps.sif", b);
            Assert.Contains("centralBank.inbox.ack.message: uae.wps.ack", b);
            Assert.DoesNotMatch(@"(?i)password:|BEGIN .*PRIVATE KEY", b);
        }
        Assert.Contains("COPY --chown=root:root deploy/laptop/banks/ /opt/th/deploy/laptop/banks/", Dockerfile);
    }

    static string ServicesOf(string text) => text[text.IndexOf("\nservices:\n", StringComparison.Ordinal)..];

    [Fact]
    public void No_host_migrates_from_the_runtimes_login_and_the_migrate_step_brings_Temporals_schemas_up_to_date()
    {
        // R-09-141: the migrate step (db-init) migrates the store, the read model and Temporal's two databases as the owner, once; every
        // service that connects as th_runtime runs with Store__Migrate=false, and the Temporal image sets up no schema.
        var dbInit = Service("db-init");
        Assert.Contains("command: [\"migrate\"]", dbInit);
        Assert.Contains("TH_TEMPORAL_SCHEMA_PLUGIN: sqlserver", dbInit);
        Assert.Contains("TH_TEMPORAL_SCHEMA_PASSWORD_REF: env:", dbInit);
        Assert.DoesNotContain("temporal-schema", Compose);
        Assert.DoesNotContain("setup-schema", File.ReadAllText(Path.Combine(Laptop, "temporal", "start.sh")));
        Assert.Matches(@"x-store: &store\n(?:  .*\n)*?  Store__Migrate: ""false""", Compose);
        var checkedHosts = 0;
        foreach (Match m in Regex.Matches(ServicesOf(Compose), @"^  ([a-z-]+):(?: &\S+)?\n((?:    .*\n|\n)*?)(?=^  \S|^\S|\z)", RegexOptions.Multiline))
        {
            var (name, body) = (m.Groups[1].Value, m.Groups[2].Value);
            var viaAnchor = Regex.IsMatch(body, @"<<: \[?\*store\b");
            if (!body.Contains("th_runtime") && !viaAnchor) continue;
            checkedHosts++;
            Assert.True(viaAnchor || body.Contains("Store__Migrate: \"false\""), $"{name} connects as the runtime's login but may migrate the store");
        }
        Assert.True(checkedHosts >= 10, $"only {checkedHosts} services connect as the runtime's login");
        // The runtime image carries the schema tool and both databases' schema files for the migrate step.
        Assert.Contains("COPY --from=temporal-bits /out/temporal-sql-tool /opt/th/temporal/bin/", Dockerfile);
        Assert.True(Directory.Exists(Path.Combine(Laptop, "temporal", "schema", "postgresql", "v12", "temporal", "versioned")));
    }

    [Fact]
    public void The_PostgreSQL_variant_runs_the_store_the_read_model_and_Temporal_on_PostgreSQL_with_the_same_grants()
    {
        // R-09-140: an override of compose.yaml; its images pinned, no secret value, ports on loopback; its grants SQL Server's.
        var pg = File.ReadAllText(Path.Combine(Laptop, "compose.postgres.yaml"));
        var images = Regex.Matches(pg, @"^\s+image:\s*(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(images);
        Assert.All(images, i => Assert.Contains("@sha256:", i));
        foreach (Match m in Regex.Matches(pg, @"Password=([^;""]*)")) Assert.StartsWith("${", m.Groups[1].Value);
        foreach (Match m in Regex.Matches(pg, @"ports:\s*\[(.*?)\]")) Assert.StartsWith("\"127.0.0.1:", m.Groups[1].Value.Trim());
        Assert.Contains("TH_TEMPORAL_SCHEMA_PLUGIN: postgres12", pg);
        Assert.Contains("TEMPORAL_DB_PLUGIN: postgres12", pg);
        Assert.Contains("grants-postgres.sql", pg);
        foreach (var svc in new[] { "runtime-api", "service-api", "ops-api", "native", "file-api", "testbench", "enginehost", "enginehost-dtfx", "enginehost-elsa", "enginehost-flowable" })
            Assert.Matches($@"(?m)^  {Regex.Escape(svc)}:", pg);
        var grants = File.ReadAllText(Path.Combine(Laptop, "sql", "grants-postgres.sql"));
        var store = grants[grants.IndexOf("\\connect th_store", StringComparison.Ordinal)..grants.IndexOf("\\connect th_ops", StringComparison.Ordinal)];
        Assert.Contains("GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA public TO th_runtime;", store);
        Assert.Contains("REVOKE UPDATE, DELETE, TRUNCATE", store);
        Assert.DoesNotMatch(@"GRANT[^;]*(UPDATE|DELETE|TRUNCATE|ALL PRIVILEGES)[^;]*TABLES[^;]*th_runtime", store);
    }

    [Fact]
    public void The_development_identity_provider_is_refused_in_Production()
    {
        var project = Path.Combine(Root(), "runtime", "tools", "TransactHub.DevIdentity");
        var build = Process.Start(new ProcessStartInfo("dotnet", ["build", project, "--nologo", "-v", "q"]) { RedirectStandardOutput = true })!;
        var built = build.StandardOutput.ReadToEnd();
        Assert.True(build.WaitForExit(300000) && build.ExitCode == 0, built);
        var dll = Path.Combine(project, "bin", "Debug", "net10.0", "TransactHub.DevIdentity.dll");
        Assert.True(File.Exists(dll), dll);
        var psi = new ProcessStartInfo("dotnet", [dll, "serve"]) { RedirectStandardError = true, RedirectStandardOutput = true };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(60000));
        Assert.Equal(2, p.ExitCode);
        Assert.Contains("refused in Production", err);
    }
}
