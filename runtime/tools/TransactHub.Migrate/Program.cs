using Microsoft.EntityFrameworkCore;
using TransactHub.Operations;
using TransactHub.Security;
using TransactHub.Store;

// Applies the store's migrations and the operations read model's, once, and exits (deploy/laptop, R-09-78). It connects as the
// databases' owner; the runtime's hosts then run with Store:Migrate=false as a user granted SELECT and INSERT on the store and
// denied UPDATE and DELETE, so that what the store holds is never changed or removed (insert-only, plan W2), by the database's
// own permissions as well as by the store's guard.
//
//   TH_STORE_PROVIDER (SqlServer|PostgreSql), TH_STORE_CONNECTION_REF and TH_OPS_CONNECTION_REF: the connections, by reference
//   (env:, file:, vault: ...), never as values in a file. Operations:Views: the published artefacts the read model's views are in.

var provider = Enum.Parse<StoreProvider>(Environment.GetEnvironmentVariable("TH_STORE_PROVIDER") ?? "SqlServer", ignoreCase: true);
var secrets = SecretResolver.FromEnvironment(Environment.GetEnvironmentVariable);
async Task<string> Connection(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } r ? (await secrets.Resolve(r)).Reveal() : throw new InvalidOperationException($"Set {name}: a reference to the connection");
var store = new StoreOptions(provider, await Connection("TH_STORE_CONNECTION_REF"));
var ops = new StoreOptions(provider, await Connection("TH_OPS_CONNECTION_REF"));
for (var attempt = 1; ; attempt++)
{
    try
    {
        await using (var db = StoreDbContext.Open(store)) await db.Database.MigrateAsync();
        var views = ViewCatalog.FromFolder(Environment.GetEnvironmentVariable("Operations__Views") ?? "/opt/th/content-configurations/artefacts");
        await new Projector(store, ops, views).Migrate();
        Console.WriteLine($"migrated the store and the read model ({provider})");
        return 0;
    }
    catch (Exception e) when (attempt < 30)
    {
        Console.Error.WriteLine($"migrate: attempt {attempt}: {e.GetType().Name}: {e.Message.Split('\n')[0]}");
        await Task.Delay(TimeSpan.FromSeconds(3));
    }
}
