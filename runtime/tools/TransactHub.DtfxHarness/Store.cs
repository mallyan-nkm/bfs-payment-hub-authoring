using System.Runtime.CompilerServices;
using TransactHub.Engines.Durable;

namespace TransactHub.DtfxHarness;

static class Store
{
    /// <summary>Before the harness's Main: the store journal, when the deployment names a store.</summary>
    [ModuleInitializer]
    internal static void Install() => StoreJournal.InstallFromEnvironment();
}
