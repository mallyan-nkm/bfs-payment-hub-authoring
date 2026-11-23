using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TransactHub.Operations;
using TransactHub.Security;
using TransactHub.Store;

// The installation's migrate step (deploy/laptop, R-09-81, R-09-141): applies the store's migrations, the operations read model's
// and, when asked, Temporal's schemas, once, at the installation's start, and exits. It connects as the databases' owner; the
// runtime's hosts then run with Store:Migrate=false as a user granted SELECT and INSERT on the store and denied UPDATE and DELETE,
// so that what the store holds is never changed or removed (insert-only, plan W2), by the database's own permissions as well as by
// the store's guard, and the Temporal server as a login that holds data rights only. No host migrates from the runtime's login.
//
//   TH_STORE_PROVIDER (SqlServer|PostgreSql), TH_STORE_CONNECTION_REF and TH_OPS_CONNECTION_REF: the connections, by reference
//   (env:, file:, vault: ...), never as values in a file. Operations:Views: the published artefacts the read model's views are in.
//   TH_TEMPORAL_SCHEMA_PLUGIN (sqlserver|postgres12): bring Temporal's two databases to the schema of the server's version, with
//   temporal-sql-tool (TH_TEMPORAL_SQL_TOOL, default /opt/th/temporal/bin/temporal-sql-tool) and its schema files
//   (TH_TEMPORAL_SCHEMA_DIR, default /opt/th/temporal/schema); TH_TEMPORAL_SCHEMA_ADDR (host:port), TH_TEMPORAL_SCHEMA_USER and
//   TH_TEMPORAL_SCHEMA_PASSWORD_REF (by reference) the owner; TEMPORAL_DB and TEMPORAL_VISIBILITY_DB the databases' names.

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
        break;
    }
    catch (Exception e) when (attempt < 30)
    {
        Console.Error.WriteLine($"migrate: attempt {attempt}: {e.GetType().Name}: {e.Message.Split('\n')[0]}");
        await Task.Delay(TimeSpan.FromSeconds(3));
    }
}

if (Environment.GetEnvironmentVariable("TH_TEMPORAL_SCHEMA_PLUGIN") is { Length: > 0 } plugin)
{
    var tool = Environment.GetEnvironmentVariable("TH_TEMPORAL_SQL_TOOL") ?? "/opt/th/temporal/bin/temporal-sql-tool";
    var dir = Environment.GetEnvironmentVariable("TH_TEMPORAL_SCHEMA_DIR") ?? "/opt/th/temporal/schema";
    var addr = Environment.GetEnvironmentVariable("TH_TEMPORAL_SCHEMA_ADDR") ?? throw new InvalidOperationException("Set TH_TEMPORAL_SCHEMA_ADDR: host:port of Temporal's database server");
    var user = Environment.GetEnvironmentVariable("TH_TEMPORAL_SCHEMA_USER") ?? throw new InvalidOperationException("Set TH_TEMPORAL_SCHEMA_USER: the owner of Temporal's databases");
    var password = (await secrets.Resolve(Environment.GetEnvironmentVariable("TH_TEMPORAL_SCHEMA_PASSWORD_REF") ?? throw new InvalidOperationException("Set TH_TEMPORAL_SCHEMA_PASSWORD_REF: the owner's password, by reference"))).Reveal();
    var versioned = plugin switch
    {
        "sqlserver" => (Func<string, string>)(part => Path.Combine(dir, "sqlserver", part, "versioned")),
        "postgres12" => part => Path.Combine(dir, "postgresql", "v12", part, "versioned"),
        _ => throw new InvalidOperationException($"TH_TEMPORAL_SCHEMA_PLUGIN is {plugin}: sqlserver or postgres12"),
    };
    var host = addr[..addr.LastIndexOf(':')];
    var port = addr[(addr.LastIndexOf(':') + 1)..];
    // The password goes to the tool in its environment (SQL_PASSWORD), never on its command line.
    async Task<(int code, string output)> Tool(string db, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "--plugin", plugin, "--ep", host, "-p", port, "--db", db }.Concat(args)) psi.ArgumentList.Add(a);
        psi.Environment["SQL_USER"] = user;
        psi.Environment["SQL_PASSWORD"] = password;
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, output);
    }
    foreach (var (db, part) in new[] { (Environment.GetEnvironmentVariable("TEMPORAL_DB") ?? "temporal", "temporal"), (Environment.GetEnvironmentVariable("TEMPORAL_VISIBILITY_DB") ?? "temporal_visibility", "visibility") })
    {
        // Created once and set up once: a database or a schema that is there already is left as it is, then brought up to date.
        Console.WriteLine((await Tool(db, "create-database")).code == 0 ? $"created database {db}" : $"database {db} is there");
        Console.WriteLine((await Tool(db, "setup-schema", "-v", "0.0")).code == 0 ? $"set up the schema of {db}" : $"the schema of {db} is set up");
        var (code, output) = await Tool(db, "update-schema", "-d", versioned(part));
        if (code != 0) { Console.Error.WriteLine($"migrate: Temporal's {db}: {output}"); return 1; }
        Console.WriteLine($"brought Temporal's {db} up to date ({plugin})");
    }
}
return 0;
