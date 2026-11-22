using Microsoft.EntityFrameworkCore.Design;
using TransactHub.Store;

namespace TransactHub.Operations.Migrations.PostgreSql;

/// <summary>For dotnet ef only: the operations read model on PostgreSql. No connection is opened to add a migration.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<OpsDbContext>
{
    public OpsDbContext CreateDbContext(string[] args) => OpsDbContext.Open(new StoreOptions(StoreProvider.PostgreSql, "Host=localhost;Database=design"));
}
