using Microsoft.EntityFrameworkCore.Design;

namespace TransactHub.Store.Migrations.PostgreSql;

/// <summary>For dotnet ef only: the model on PostgreSql. No connection is opened to add a migration.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<StoreDbContext>
{
    public StoreDbContext CreateDbContext(string[] args) => StoreDbContext.Open(new StoreOptions(StoreProvider.PostgreSql, "Host=localhost;Database=design"));
}
