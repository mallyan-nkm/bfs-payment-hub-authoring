using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Wps.Rules;

namespace TransactHub.Activities.Wps.Native;

/// <summary>
/// WPS's match of the corporate the party register found (R-12-27): the source's pre-check (WpsPlatformActivities.Precheck), on
/// the ported <see cref="SifUploadRules.MatchCorporate"/>. The corporate must exist, be active and hold the WPS registration form;
/// the debit account the channel supplied must be one it registered. The source's codes become the step's scheme code: 003 is not
/// registered (RR04), 114, 010 and 165 a mismatch (BE01). Since R-09-248 (Issues I-314, I-153) the CIF and the e-mail the channel
/// declared at upload are compared too, each where both it and the register's are known: the debtor's organisation id of scheme CIF
/// (R-02-22) with the register's <c>cif</c> (114), and the debtor's e-mail (<c>contactDetails.emailAddress</c>, R-02-23) with the
/// register's <c>emails</c> (165). The e-mail is personal data: it is read from the payment's view and never written to the step's facts.
/// </summary>
public static class WpsCorporateMatch
{
    public const string Scheme = "uae.wps";
    /// <summary>The scheme of the debtor's organisation id that is its CIF at the bank (R-02-22).</summary>
    public const string CifScheme = "CIF";

    /// <summary>The CIF the channel declared: the debtor's further organisation id of scheme CIF, else none.</summary>
    public static string? SuppliedCif(JsonObject view) =>
        (NativeContext.Node(view, "batch.debtor.otherOrganisationIds") as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => string.Equals(NativeContext.S(o, "scheme"), CifScheme, StringComparison.OrdinalIgnoreCase))
            .Select(o => NativeContext.S(o, "id")).FirstOrDefault(x => x.Length > 0);

    /// <summary>The e-mail the channel declared for the debtor (Pty/CtctDtls/EmailAdr), else none.</summary>
    public static string? SuppliedEmail(JsonObject view) =>
        NativeContext.S(view, "batch.debtor.contactDetails.emailAddress") is { Length: > 0 } e ? e : null;

    public static PartyMatch? Match(string id, NativeParty? party, JsonObject view)
    {
        SifRegisteredCorporate? corporate = null;
        var suppliedAccount = NativeContext.S(view, "batch.debtorAccount.iban") is { Length: > 0 } a ? a : NativeContext.S(view, "batch.debtorAccount.other");
        string? suppliedCif = null, suppliedEmail = null;
        if (party is { } p && (bool?)p.Record["active"] == true && (bool?)p.Record["wpsRegistrationFormHeld"] == true)
        {
            var accounts = (p.Record["accounts"] as JsonArray ?? []).Select(x => (string?)x).Where(x => x is { Length: > 0 }).ToList();
            // The source registered one account per corporate; the register holds several: the supplied one is compared with the
            // registered account it matches, or the first when none does. So are the e-mails.
            var account = accounts.FirstOrDefault(x => string.Equals(x, suppliedAccount, StringComparison.OrdinalIgnoreCase)) ?? accounts.FirstOrDefault();
            var cif = (string?)p.Record["cif"] is { Length: > 0 } c ? c : null;
            var emails = (p.Record["emails"] as JsonArray ?? []).Select(x => (string?)x).Where(x => x is { Length: > 0 }).ToList();
            var email = emails.FirstOrDefault(x => string.Equals(x?.Trim(), SuppliedEmail(view)?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? emails.FirstOrDefault();
            // Each compared only where both are known (R-09-248): a register with no CIF or no e-mail does not refuse a file that names one.
            if (cif is not null) suppliedCif = SuppliedCif(view);
            if (email is not null) suppliedEmail = SuppliedEmail(view);
            corporate = new SifRegisteredCorporate(p.Key, cif, account, email, (string?)p.Record["corporateType"]);
        }
        var failure = SifUploadRules.MatchCorporate(id, corporate, suppliedCif, suppliedAccount.Length > 0 ? suppliedAccount : null, suppliedEmail);
        if (failure is null) return null;
        return failure.ErrorCode == "003"
            ? new PartyMatch("not-registered", "RR04", failure.ErrorCode, failure.Message)
            : new PartyMatch("mismatch", "BE01", failure.ErrorCode, failure.Message);
    }

    public static IReadOnlyDictionary<string, PartyMatcher> Matchers { get; } = new Dictionary<string, PartyMatcher> { [Scheme] = Match };
}
