using TransactHub.Ports;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// The bindings a runtime's host reads (spec R-09-112, R-09-113): those of the bank's configuration releases in force in the store,
/// read live, so a binding a new configuration release brings takes effect as of each attempt's time with no restart. A host of
/// the runtime never reads a bank folder (<c>TH_BANKS_DIR</c>): it refuses to start with one (<see cref="RefuseFolder"/>).
/// </summary>
public static class StoreBindings
{
    /// <summary>The bank's bindings in force, read from the store at most once per <paramref name="refresh"/>.</summary>
    public static PortBindings Live(StoreOptions store, string? bank, TimeSpan? refresh = null) =>
        PortBindings.Live(() => ConfigurationBindings.Load(store, bank).Select(ToPort).ToList(), refresh);

    public static PortBinding ToPort(StoredBinding b) => new(b.Tenant, b.Port, b.Adapter, b.ValidFrom, b.Settings, b.Release);

    /// <summary>Stops a host of the runtime that is given a bank folder for its bindings: they come from the configuration releases in force.</summary>
    public static void RefuseFolder(string? folder, string setting)
    {
        if (!string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException($"{setting} names a folder of port bindings, but a runtime's host reads its bank's bindings only from the configuration release in force, signed and put in force with a bank approver's consent (spec R-09-112, R-09-113): remove {setting} and install the bank's configuration release");
    }
}
