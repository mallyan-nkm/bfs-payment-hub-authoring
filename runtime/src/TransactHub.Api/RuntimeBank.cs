using Microsoft.Extensions.Configuration;

namespace TransactHub.Api;

/// <summary>
/// The one bank a runtime serves (owner's decision of 23 November 2026, spec R-09-76). The runtime is not multi-tenant: it is
/// configured with its bank at install (<c>TH_BANK</c>, or <c>Runtime:Bank</c>) and every host that lets content in refuses to
/// start without it. A distribution, a configuration release, a workflow package exported for a tenant, a bank's values, a
/// payment, a kept file or a backup that names another bank is refused, at verify, install and activate alike, whatever the
/// caller's roles are. The studio stays multi-tenant; a runtime holds one bank's content only.
/// </summary>
public sealed record RuntimeBank(string Id)
{
    public const string Setting = "TH_BANK";

    /// <summary>The bank from the host's configuration. Throws, so the host does not start, when none is set.</summary>
    public static RuntimeBank FromConfiguration(IConfiguration cfg)
    {
        var bank = cfg[Setting] is { Length: > 0 } b ? b : cfg["Runtime:Bank"];
        if (string.IsNullOrWhiteSpace(bank))
            throw new InvalidOperationException("Set TH_BANK: the one bank this runtime serves, configured at install. A runtime refuses another bank's distributions and configuration releases (spec R-09-76)");
        if (bank.Trim() is "*" or "") throw new InvalidOperationException("TH_BANK names one bank, not every bank (spec R-09-76)");
        return new RuntimeBank(bank.Trim());
    }

    /// <summary>Whether a bank named by content is this runtime's.</summary>
    public bool Serves(string? bank) => string.Equals(bank, Id, StringComparison.Ordinal);

    /// <summary>Why content of <paramref name="bank"/> is refused here, or null when it is this runtime's.</summary>
    public string? Refusal(string? bank, string what) =>
        Serves(bank) ? null : $"{what} names bank {bank ?? "(none)"}, but this runtime serves {Id} only (TH_BANK, set at install): it is refused (R-09-76)";

    /// <summary>Refuses content of another bank with 422.</summary>
    public void Check(string? bank, string what)
    {
        if (Refusal(bank, what) is { } why) throw new BundleRefused(422, why);
    }
}
