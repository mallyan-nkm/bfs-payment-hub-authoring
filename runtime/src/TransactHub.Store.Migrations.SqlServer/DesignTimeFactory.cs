using Microsoft.EntityFrameworkCore.Design;

namespace TransactHub.Store.Migrations.SqlServer;

/// <summary>For dotnet ef only: the model on SqlServer. No connection is opened to add a migration.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<StoreDbContext>
{
    public StoreDbContext CreateDbContext(string[] args) => StoreDbContext.Open(new StoreOptions(StoreProvider.SqlServer, "Server=localhost;Database=design;TrustServerCertificate=true"));
}
