using TransactHub.Store;

namespace TransactHub.Api;

/// <summary>
/// The earlier per-scope workflow packages and their activations (plan W4) beside a bank's distributions (spec R-09-114, Issue
/// I-212). A distribution is the deployment unit (docs/runtime/PACKAGING-AND-DEPLOYMENT.md, section 2): once the runtime's bank
/// has one installed, a workflow package is neither stored nor activated, by the release API or the service console, so
/// nothing runs beside the distribution that is not part of it. A runtime with no distribution yet (a test bench, the studio's
/// deploy runner) still takes workflow packages; those already stored stay as the record they are.
/// </summary>
public static class WorkflowPackages
{
    /// <summary>Why <paramref name="what"/> (a workflow package) is refused, or null when the bank has no distribution installed.</summary>
    public static async Task<string?> Refusal(StoreDbContext db, string bank, string what, CancellationToken ct = default) =>
        (await new DistributionRegistry(db).Of(bank, ct)) is { Count: > 0 } dists
            ? $"{what} is a workflow package, but this runtime runs {bank}'s distributions ({dists[0]}{(dists.Count > 1 ? $" and {dists.Count - 1} more" : "")}): a workflow package is not installed or activated beside them; its workflows reach the runtime in a distribution (R-09-114)"
            : null;
}
