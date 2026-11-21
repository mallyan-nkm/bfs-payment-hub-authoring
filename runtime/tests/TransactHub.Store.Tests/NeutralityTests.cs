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
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("TransactHub.Store.Migrations.")) continue;
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
