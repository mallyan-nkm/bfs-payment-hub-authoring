using Microsoft.EntityFrameworkCore.Design;
using TransactHub.Store;

namespace TransactHub.Operations.Migrations.SqlServer;

/// <summary>For dotnet ef only: the operations read model on SqlServer. No connection is opened to add a migration.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<OpsDbContext>
{
    public OpsDbContext CreateDbContext(string[] args) => OpsDbContext.Open(new StoreOptions(StoreProvider.SqlServer, "Server=x;Database=design;TrustServerCertificate=true"));
}
