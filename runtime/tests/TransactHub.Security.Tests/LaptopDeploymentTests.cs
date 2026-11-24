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

    /// <summary>
    /// R-09-181 (Issue I-225): an image beyond a laptop signs its native modules with NEST's module signing key, given to the build as
    /// a BuildKit secret mounted for the signing step only, never an ARG, an ENV or a file of a layer; with that mode and no secret the
    /// build stops. The key the build makes itself is the laptop's only, and build.sh passes NEST's key as a secret when it is named.
    /// </summary>
    [Fact]
    public void A_production_image_build_takes_the_module_signing_key_as_a_BuildKit_secret_and_never_makes_one()
    {
        var source = Dockerfile[Dockerfile.IndexOf("AS dotnet-source\n", StringComparison.Ordinal)..Dockerfile.IndexOf("AS ops-console-source", StringComparison.Ordinal)];
        Assert.Contains("ARG TH_MODULE_SIGNING=laptop", Dockerfile);
        Assert.Contains("RUN --mount=type=secret,id=th_native_module_key,required=false ", source);
        string Branch(string label)
        {
            var at = source.IndexOf(label + ")", StringComparison.Ordinal);
            return source[at..source.IndexOf(";;", at, StringComparison.Ordinal)];
        }
        // secret: the mounted key, read into the step's environment; the build stops without it, before any key is made.
        var secret = Branch("secret");
        Assert.Contains("[ -s /run/secrets/th_native_module_key ] || {", secret);
        Assert.Contains("exit 2", secret);
        Assert.Contains("TH_NATIVE_SIGNING_KEY=env:TH_NEST_MODULE_KEY", secret);
        Assert.DoesNotContain("genpkey", secret);
        // The self-made key only in the laptop's branch; nothing else makes one.
        Assert.Contains("genpkey", Branch("laptop"));
        Assert.Single(Regex.Matches(source, "genpkey"));
        Assert.DoesNotMatch(@"(?im)^\s*(ENV|ARG)\s+\S*KEY\S*=", Dockerfile);
        var build = File.ReadAllText(Path.Combine(Laptop, "build.sh"));
        Assert.Contains("--secret \"id=th_native_module_key,src=$KEY_FILE\" --build-arg TH_MODULE_SIGNING=secret", build);
        Assert.Contains("unset MODULE_KEY", build);
        Assert.Contains("TH_NATIVE_MODULE_KEY_FILE", File.ReadAllText(Path.Combine(Laptop, "README.md")));
    }

    /// <summary>
    /// R-09-262: the laptop image's modules are built and signed against the native contract the image carries, 2.1.0 (R-09-276), read from the
    /// contract project's Version by runtime/tools/native-module/build.sh, never written in the laptop's files; both builds of the image
    /// (in Docker, and --local) build the platform's and WPS's modules that way.
    /// </summary>
    [Fact]
    public void The_laptops_module_builds_name_the_native_contract_the_image_carries()
    {
        var csproj = File.ReadAllText(Path.Combine(Root(), "runtime", "src", "TransactHub.Activities.Contract", "TransactHub.Activities.Contract.csproj"));
        Assert.Equal(TransactHub.Activities.Native.NativeContract.Version, Regex.Match(csproj, "<Version>([^<]+)</Version>").Groups[1].Value);
        Assert.Equal("2.1.0", TransactHub.Activities.Native.NativeContract.Version);
        var tool = File.ReadAllText(Path.Combine(Root(), "runtime", "tools", "native-module", "build.sh"));
        Assert.Contains("TransactHub.Activities.Contract/TransactHub.Activities.Contract.csproj", tool);
        Assert.Contains("\"contract\": \"$contract\"", tool);
        var build = File.ReadAllText(Path.Combine(Laptop, "build.sh"));
        foreach (var text in new[] { Dockerfile, build })
        {
            Assert.Contains("runtime/tools/native-module/build.sh p runtime/src/TransactHub.Activities.Platform.Native", text);
            Assert.Contains("runtime/tools/native-module/build.sh uae.wps runtime/src/TransactHub.Activities.Wps", text);
        }
        foreach (var f in Directory.EnumerateFiles(Laptop, "*", SearchOption.AllDirectories).Where(f => !f.Contains("/.build/") && !f.Contains("node_modules")))
            Assert.DoesNotMatch(@"contract[""']?\s*[:=]\s*[""']?1\.\d+\.\d+", File.ReadAllText(f));
    }

    /// <summary>
    /// R-09-187 (Issue I-238): the front door's one-off run reads the bank's values from the release API (<c>GET /values</c>) as of the
    /// payment's date, not the bank folder's configuration set, and runs the message and file its caller names, with no scheme's default.
    /// </summary>
    [Fact]
    public void The_one_off_run_reads_the_values_in_force_from_the_release_API_and_names_its_message()
    {
        var run = File.ReadAllText(Path.Combine(Laptop, "bin", "run-pinned.mts"));
        Assert.Contains("/values?tenant=", run);
        Assert.DoesNotContain("configuration-set.yaml", run);
        Assert.DoesNotContain("uae.wps", run);
        Assert.DoesNotContain(".sif", run);
        Assert.Contains("Name the start message", run);
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
        // A scheme the bank licenses later (one built in the studio, I-205) has its intake map run on the same workers.
        Assert.Contains("FileApi__Workers__*: http://workers:7700/{distribution}/_/any", fileApi);
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
        // The outward salary file leaves through the File API (R-09-162): ADIB's release binds the WPS port to it, the bench gives the
        // port to the runtime, and the workers give the render, the send and the ACK's reading to the native host and read a received
        // file from the File API.
        var adib = File.ReadAllText(Path.Combine(Laptop, "banks", "adib", "file-transfer.yaml"));
        Assert.Matches(@"- port: uae\.wps\.port\.wps\.v1\n    adapter: file-transfer\n", adib);
        Assert.Contains("submit-file.folder: centralBank.sifFolder", adib);
        Assert.DoesNotContain("stemDrop", adib); // the scheme's correlation map reads the ACK's key (R-09-212, Issue I-267)
        var workers = Service("workers");
        Assert.Contains("{\"port\":\"uae.wps.port.wps.v1\",\"simulator\":\"runtime\"}", workers);
        Assert.Contains("TH_NATIVE_HOST: http://native:7300", workers);
        Assert.Contains("TH_NATIVE_ACTIVITIES: uae.wps.map.sif-out.v1,uae.wps.exchange.send-salary-file.v1,uae.wps.exchange.send-salary-file.v2,uae.wps.map.ack-in.v1", workers);
        Assert.Contains("TH_FILE_API_URL: http://file-api:8080", workers);
        Assert.Matches(@"TH_NATIVE_TOKEN: file:\S+", workers);
        Assert.Contains("'TH_NATIVE_TOKEN', 'TH_FILE_API_URL', 'TH_FILE_API_TOKEN'", File.ReadAllText(Path.Combine(Laptop, "bin", "workers.mts")));
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
