using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace TransactHub.TestBench;

/// <summary>
/// What the engines of a run need, started for it and stopped after: the store's database server (Testcontainers, or a
/// configured connection), the Flowable REST server (a container, or a configured URL), Temporal (the time-skipping test
/// server the harness starts, or a configured address), and the harnesses built. One instance serves a whole suite, and
/// the service keeps one for its life; each engine run gets a database of its own on the server.
/// </summary>
public sealed class Infra(Home home, Action<string>? log = null) : IAsyncDisposable
{
    /// <summary>The Flowable REST server of the runtime's tests (studio/test/blackbox/e2e-wps-engines-store.test.ts).</summary>
    public const string FlowableImage = "flowable/flowable-rest:7.1.0@sha256:b7ae287502cd5c243b577a0ca2894172e1c18cd75aea4fd67dc7d58f1b40db44";
    const string FlowableUser = "th-testbench";
    // Test-only credentials of containers that live for one run and listen on 127.0.0.1 only.
    const string FlowablePassword = "th-test-only";
    const string DbPassword = "Th-Test-Only-1234";

    readonly SemaphoreSlim gate = new(1, 1);
    readonly Dictionary<string, string> servers = new();
    readonly List<IAsyncDisposable> containers = new();
    readonly HashSet<string> built = new();
    Dictionary<string, string>? flowable;
    readonly string tag = $"{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}";

    void Log(string s) => log?.Invoke(s);

    /// <summary>The connection of a fresh database for one engine run, on the provider's server.</summary>
    public async Task<string> Database(string provider, string database, IReadOnlyDictionary<string, string> configured, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!servers.TryGetValue(provider, out var server))
            {
                if (configured.TryGetValue(provider, out var c)) { server = c; Log($"{provider}: the configured server"); }
                else server = provider == "PostgreSql" ? await Postgres(ct) : await SqlServer(ct);
                servers[provider] = server;
            }
            return WithDatabase(server, database);
        }
        finally { gate.Release(); }
    }

    /// <summary>A connection string with its database replaced (each engine run has its own, created by the store's migrations).</summary>
    public static string WithDatabase(string connection, string database)
    {
        var parts = connection.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(p => !Regex.IsMatch(p.Trim(), @"^(Database|Initial Catalog)\s*=", RegexOptions.IgnoreCase));
        return string.Join(';', parts.Append($"Database={database}"));
    }

    /// <summary>The same connection, its password hidden, for a report.</summary>
    public static string Redact(string connection) => Regex.Replace(connection, @"(Password|Pwd)\s*=[^;]*", "$1=***", RegexOptions.IgnoreCase);

    async Task<string> Postgres(CancellationToken ct)
    {
        Log("PostgreSql: starting postgres:16-alpine (Testcontainers)");
        var c = new PostgreSqlBuilder("postgres:16-alpine").WithName($"th-testbench-pg-{tag}").WithPassword(DbPassword).Build();
        containers.Add(c);
        await c.StartAsync(ct);
        return $"Host={c.Hostname};Port={c.GetMappedPublicPort(5432)};Username=postgres;Password={DbPassword}";
    }

    async Task<string> SqlServer(CancellationToken ct)
    {
        Log("SqlServer: starting SQL Server 2022 (Testcontainers)");
        var c = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").WithName($"th-testbench-mssql-{tag}").WithPassword(DbPassword).Build();
        containers.Add(c);
        await c.StartAsync(ct);
        return $"Server={c.Hostname},{c.GetMappedPublicPort(1433)};User Id=sa;Password={DbPassword};TrustServerCertificate=true";
    }

    /// <summary>The Flowable REST server's environment for the harness: the configured URL, or a container started once.</summary>
    public async Task<Dictionary<string, string>> Flowable(string setting, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (flowable is not null) return flowable;
            if (setting != "container")
            {
                var env = new Dictionary<string, string> { ["TH_FLOWABLE_URL"] = setting };
                foreach (var k in new[] { "TH_FLOWABLE_USER", "TH_FLOWABLE_PASSWORD" }) if (Environment.GetEnvironmentVariable(k) is { Length: > 0 } v) env[k] = v;
                return flowable = env;
            }
            Log("Flowable: starting the Flowable REST server 7.1.0 (Testcontainers), its async executor off");
            var c = new ContainerBuilder(FlowableImage).WithName($"th-testbench-flowable-{tag}")
                .WithEnvironment("FLOWABLE_ASYNC_EXECUTOR_ACTIVATE", "false").WithEnvironment("FLOWABLE_ASYNC_HISTORY_EXECUTOR_ACTIVATE", "false")
                .WithEnvironment("FLOWABLE_REST_APP_CREATE_DEMO_DEFINITIONS", "false")
                .WithEnvironment("FLOWABLE_REST_APP_ADMIN_USER_ID", FlowableUser).WithEnvironment("FLOWABLE_REST_APP_ADMIN_PASSWORD", FlowablePassword)
                .WithPortBinding(8080, true).Build();
            containers.Add(c);
            await c.StartAsync(ct);
            var url = $"http://{c.Hostname}:{c.GetMappedPublicPort(8080)}/flowable-rest";
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{FlowableUser}:{FlowablePassword}")));
            for (var i = 0; ; i++)
            {
                try { if ((await http.GetAsync($"{url}/service/management/engine", ct)).IsSuccessStatusCode) break; } catch (HttpRequestException) { }
                if (i > 240) throw new BenchError("The Flowable REST server did not start in four minutes");
                await Task.Delay(1000, ct);
            }
            return flowable = new() { ["TH_FLOWABLE_URL"] = url, ["TH_FLOWABLE_USER"] = FlowableUser, ["TH_FLOWABLE_PASSWORD"] = FlowablePassword };
        }
        finally { gate.Release(); }
    }

    /// <summary>Temporal's environment for the harness: an address, or nothing (the harness starts the time-skipping test server).</summary>
    public Dictionary<string, string> Temporal(string setting)
    {
        if (setting != "test-server") return new() { ["TH_TEMPORAL_ADDRESS"] = setting };
        var dir = Path.Combine(home.Tools, "temporal-test-server");
        if (!File.Exists(Path.Combine(dir, "classpath.txt")))
        {
            Log("Temporal: setting up the time-skipping test server (runtime/tools/temporal-test-server/setup.sh)");
            Run(Path.Combine(dir, "setup.sh"), "", dir);
        }
        return new();
    }

    /// <summary>Builds each harness a run needs, once per process: the runtime's builds with a store, the studio's DTFx and Elsa without.</summary>
    public void Build(IEnumerable<string> engines, bool onStore)
    {
        foreach (var e in engines)
        {
            var dir = e switch
            {
                "temporal" => Path.Combine(home.Tools, "TransactHub.TemporalHarness"),
                "flowable" => Path.Combine(home.Tools, "TransactHub.FlowableHarness"),
                "dtfx" => onStore ? Path.Combine(home.Tools, "TransactHub.DtfxHarness") : Path.Combine(home.Studio, "engines", "dtfx", "harness"),
                "elsa" => onStore ? Path.Combine(home.Tools, "TransactHub.ElsaHarness") : Path.Combine(home.Studio, "engines", "elsa", "harness"),
                _ => throw new BenchError($"No engine {e}"),
            };
            lock (built) { if (!built.Add(dir)) continue; }
            Log($"building {Path.GetRelativePath(home.Repo, dir)}");
            Run("dotnet", "build -c Release --nologo -v q", dir);
        }
    }

    public void CheckStudio()
    {
        if (!File.Exists(Path.Combine(home.Studio, "node_modules", ".bin", "tsx")))
            throw new BenchError("The studio's packages are not installed: cd studio && npm install");
    }

    static void Run(string file, string args, string cwd)
    {
        var psi = new ProcessStartInfo(file, args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new BenchError($"{file} {args} in {cwd} failed:\n{o.Result}\n{e.Result}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in containers) { try { await c.DisposeAsync(); } catch { } }
        containers.Clear();
    }
}
