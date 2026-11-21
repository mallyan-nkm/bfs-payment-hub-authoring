using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

/// <summary>
/// SQL Server 2022 and PostgreSQL 16, one container each for the whole run. Each test gets a database of
/// its own, created from the model, so tests never see each other's rows.
/// </summary>
public sealed class Databases : IAsyncLifetime
{
    readonly MsSqlContainer sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    readonly PostgreSqlContainer pg = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync() => await Task.WhenAll(sql.StartAsync(), pg.StartAsync());
    public async Task DisposeAsync() => await Task.WhenAll(sql.DisposeAsync().AsTask(), pg.DisposeAsync().AsTask());

    public StoreOptions Fresh(StoreProvider p)
    {
        var name = $"th_{Guid.NewGuid():N}"[..20];
        return p switch
        {
            StoreProvider.SqlServer => new(p, new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sql.GetConnectionString()) { InitialCatalog = name, TrustServerCertificate = true }.ConnectionString),
            _ => new(p, new Npgsql.NpgsqlConnectionStringBuilder(pg.GetConnectionString()) { Database = name }.ConnectionString),
        };
    }

    public async Task<StoreDbContext> Open(StoreProvider p)
    {
        var db = StoreDbContext.Open(Fresh(p));
        await db.Database.MigrateAsync();
        return db;
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<Databases> { public const string Name = "databases"; }

public static class Providers
{
    public static TheoryData<StoreProvider> Both => new() { StoreProvider.SqlServer, StoreProvider.PostgreSql };
}

/// <summary>A clock the test moves.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset now = start;
    public override DateTimeOffset GetUtcNow() => now;
    public void Set(DateTimeOffset t) => now = t;
    public void Advance(TimeSpan d) => now += d;
}
