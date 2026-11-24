using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

/// <summary>
/// Database independence (plan W2): the runtime reaches data only through EF Core. A driver, raw SQL or a
/// T-SQL idiom anywhere in runtime/src fails the build, outside the two provider lines in StoreDbContext
/// and the generated migrations of each provider.
/// </summary>
public sealed class NeutralityTests
{
    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TransactHub.Runtime.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("runtime root");
    }

    static readonly (string What, Regex Pattern)[] Forbidden =
    [
        ("a database driver", new(@"\b(Microsoft\.Data\.SqlClient|System\.Data\.SqlClient|Npgsql)\b")),
        ("a raw connection", new(@"\b(SqlConnection|NpgsqlConnection|SqlCommand|NpgsqlCommand|DbCommand)\b")),
        ("raw SQL through EF", new(@"\b(FromSqlRaw|FromSqlInterpolated|FromSql|ExecuteSqlRaw|ExecuteSqlInterpolated|ExecuteSql|SqlQueryRaw|SqlQuery)\s*[<(]")),
        ("a T-SQL idiom", new(@"\b(MERGE\s+INTO|WITH\s*\(\s*(NOLOCK|UPDLOCK|HOLDLOCK|READPAST)|OUTPUT\s+INSERTED|SYSUTCDATETIME|GETUTCDATE|SqlDbType\.Structured)\b", RegexOptions.IgnoreCase)),
        ("a provider call", new(@"\b(UseSqlServer|UseNpgsql)\s*\(")),
    ];

    [Fact]
    public void No_source_file_reaches_a_database_except_through_EF_Core()
    {
        var src = Path.Combine(Root(), "src");
        var problems = new List<string>();
        foreach (var f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f).Replace('\\', '/');
            // The generated migrations of each provider: the store's and the operations read model's (R-05-49).
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("TransactHub.Store.Migrations.") || rel.StartsWith("TransactHub.Operations.Migrations.")) continue;
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//") || line.TrimStart().StartsWith("///")) continue;
                if (rel == "TransactHub.Store/StoreDbContext.cs" && Regex.IsMatch(line, @"StoreProvider\.(SqlServer|PostgreSql) => b\.Use(SqlServer|Npgsql)\(")) continue;
                foreach (var (what, p) in Forbidden)
                    if (p.IsMatch(line)) problems.Add($"{rel}:{i + 1}: {what}: {line.Trim()}");
            }
        }
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// R-09-142: platform and engine host code names no scheme. A scheme's values (its channel, its awaited replies, its waits) are the
    /// scheme pack's declared values or the bank's configuration; only the scheme's own projects (TransactHub.Activities.Wps, TransactHub.Activities.Wps.Store)
    /// and the generated migrations may name one. The one place left is listed with its issue.
    /// </summary>
    [Fact]
    public void No_platform_or_engine_host_code_names_a_scheme()
    {
        var root = Root();
        var allowed = new Dictionary<string, string>
        {
            // The labour ministry's port is WPS's, but its adapters are still in the platform's ports library (Issue I-237).
            ["src/TransactHub.Ports/EmployerRegister.cs"] = "public const string Port = \"uae.wps.port.labour-ministry.v1\";",
        };
        var scheme = new Regex(@"""[^""]*(\buae\.wps\b|\bWPS\b)[^""]*""");
        var problems = new List<string>();
        foreach (var dir in new[] { "src", "tools/TransactHub.EngineHost", "tools/TransactHub.Migrate", "../studio/engines/shared" })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("src/TransactHub.Activities.Wps") || rel.Contains(".Migrations.")) continue;
                var lines = File.ReadAllLines(f);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.StartsWith("//") || !scheme.IsMatch(line)) continue;
                    if (allowed.TryGetValue(rel, out var ok) && line == ok) continue;
                    problems.Add($"{rel}:{i + 1}: {line}");
                }
            }
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// R-09-185 (Issue I-238): platform and engine host code gives no cluster's currency as a default. A currency is the payment's own,
    /// its batch's, the caller's, or a binding's required setting (<c>currency</c>, <c>localCurrency</c>), set in each bank's binding;
    /// only the scheme's own projects may name one.
    /// </summary>
    [Fact]
    public void No_platform_or_engine_host_code_defaults_a_currency()
    {
        var root = Root();
        var currency = new Regex(@"""AED""");
        var problems = new List<string>();
        foreach (var dir in new[] { "src", "tools/TransactHub.EngineHost", "tools/TransactHub.Migrate", "../studio/engines/shared" })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("src/TransactHub.Activities.Wps") || rel.Contains(".Migrations.")) continue;
                var lines = File.ReadAllLines(f);
                for (var i = 0; i < lines.Length; i++)
                    if (!lines[i].Trim().StartsWith("//") && currency.IsMatch(lines[i])) problems.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        Assert.True(problems.Count == 0, string.Join('\n', problems));
        // A simulator's or a file bulk's currency is its binding's required setting: a binding that names none is refused.
        var b = new TransactHub.Ports.PortBinding("t", "p.port.core-banking.v1", "simulator", DateTimeOffset.UnixEpoch, new Dictionary<string, string>());
        Assert.Contains("'currency'", Assert.Throws<TransactHub.Ports.PortBindingException>(() => new TransactHub.Ports.SimulatedCoreBanking(b).Balance("", "1").GetAwaiter().GetResult()).Message);
        // R-11-104: every bank folder's core-banking binding names the currency its accounts are kept in.
        var banks = Path.Combine(root, "..", "content-configurations", "banks");
        foreach (var dir in Directory.EnumerateDirectories(banks).Order(StringComparer.Ordinal))
        {
            var file = Path.Combine(dir, "core-banking.yaml");
            if (!File.Exists(file)) continue;
            var bound = new TransactHub.Ports.PortBindings();
            bound.Add(File.ReadAllText(file));
            var tenant = Path.GetFileName(dir);
            foreach (var at in new[] { DateTimeOffset.Parse("2026-11-24T00:00:00Z"), DateTimeOffset.Parse("2027-06-01T00:00:00Z") })
                Assert.True(bound.InForce(tenant, "p.port.core-banking.v1", at).Settings.ContainsKey("currency"), $"{tenant}'s core-banking binding names no currency");
        }
    }

    [Fact]
    public void Only_the_store_and_its_migrations_reference_a_provider_package()
    {
        var src = Path.Combine(Root(), "src");
        foreach (var proj in Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(proj);
            if (name is "TransactHub.Store" || name.StartsWith("TransactHub.Store.Migrations.")) continue;
            var text = File.ReadAllText(proj);
            Assert.False(Regex.IsMatch(text, @"EntityFrameworkCore\.SqlServer|Npgsql|SqlClient"), $"{name} references a database provider; only TransactHub.Store may.");
        }
    }

    [Theory]
    [InlineData(StoreProvider.SqlServer, "Server=x;Database=y;TrustServerCertificate=true")]
    [InlineData(StoreProvider.PostgreSql, "Host=x;Database=y")]
    public void Each_provider_has_migrations_for_the_whole_model(StoreProvider p, string cs)
    {
        using var db = StoreDbContext.Open(new StoreOptions(p, cs));
        Assert.NotEmpty(db.Database.GetMigrations());
        Assert.False(db.Database.HasPendingModelChanges(), $"The model changed without a new {p} migration: run dotnet ef migrations add for both providers.");
    }
}
