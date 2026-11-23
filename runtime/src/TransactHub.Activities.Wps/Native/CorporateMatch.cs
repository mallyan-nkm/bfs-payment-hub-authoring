using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Wps.Rules;
using TransactHub.Ports;

namespace TransactHub.Activities.Wps.Native;

/// <summary>
/// WPS's match of the corporate the party register found (R-12-27): the source's pre-check (WpsPlatformActivities.Precheck), on
/// the ported <see cref="SifUploadRules.MatchCorporate"/>. The corporate must exist, be active and hold the WPS registration form;
/// the debit account the channel supplied must be one it registered. The source's codes become the step's scheme code: 003 is not
/// registered (RR04), 114, 010 and 165 a mismatch (BE01). The CIF and e-mail the source's channel supplied have no home in the
/// canonical model, so they are not compared (Issue I-151).
/// </summary>
public static class WpsCorporateMatch
{
    public const string Scheme = "uae.wps";

    public static PartyMatch? Match(string id, RegisteredParty? party, JsonObject view)
    {
        SifRegisteredCorporate? corporate = null;
        if (party is { } p && (bool?)p.Record["active"] == true && (bool?)p.Record["wpsRegistrationFormHeld"] == true)
        {
            var accounts = (p.Record["accounts"] as JsonArray ?? []).Select(x => (string?)x).Where(x => x is { Length: > 0 }).ToList();
            var supplied = NativeContext.S(view, "batch.debtorAccount.iban") is { Length: > 0 } iban ? iban : NativeContext.S(view, "batch.debtorAccount.other");
            // The source registered one account per corporate; the register holds several: the supplied one is compared with the
            // registered account it matches, or the first when none does.
            var account = accounts.FirstOrDefault(x => string.Equals(x, supplied, StringComparison.OrdinalIgnoreCase)) ?? accounts.FirstOrDefault();
            corporate = new SifRegisteredCorporate(p.Key, (string?)p.Record["cif"], account, null, (string?)p.Record["corporateType"]);
        }
        var suppliedAccount = NativeContext.S(view, "batch.debtorAccount.iban") is { Length: > 0 } a ? a : NativeContext.S(view, "batch.debtorAccount.other");
        var failure = SifUploadRules.MatchCorporate(id, corporate, null, suppliedAccount.Length > 0 ? suppliedAccount : null, null);
        if (failure is null) return null;
        return failure.ErrorCode == "003"
            ? new PartyMatch("not-registered", "RR04", failure.ErrorCode, failure.Message)
            : new PartyMatch("mismatch", "BE01", failure.ErrorCode, failure.Message);
    }

    public static IReadOnlyDictionary<string, PartyMatcher> Matchers { get; } = new Dictionary<string, PartyMatcher> { [Scheme] = Match };
}
