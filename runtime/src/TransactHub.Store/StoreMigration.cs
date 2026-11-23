namespace TransactHub.Store;

/// <summary>
/// Whether a host may bring the store's schema up to date itself (R-09-141). An installation migrates once, at its start, from its
/// migrate step (runtime/tools/TransactHub.Migrate) as the databases' owner, and runs every host with <c>Store:Migrate</c>
/// (<c>Store__Migrate</c>) or <c>TH_STORE_MIGRATE</c> set to <c>false</c>, as the runtime's login, which may not change the schema. A
/// host given neither (a test, a developer's machine) migrates as before.
/// </summary>
public static class StoreMigration
{
    public static bool Allowed(Func<string, string?>? setting = null)
    {
        setting ??= Environment.GetEnvironmentVariable;
        var v = setting("Store:Migrate") ?? setting("Store__Migrate") ?? setting("TH_STORE_MIGRATE");
        return !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
    }
}
