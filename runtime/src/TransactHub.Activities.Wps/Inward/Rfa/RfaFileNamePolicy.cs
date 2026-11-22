// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/Rfa/RfaFileNamePolicy.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: names an inward RFA file (WPS inward Activity 25, design Inward_PAF_RFA_Generation v1.0 §6.2; A-5, A-8; task T2).
// Responsibility: HeadRoutingCode characters 1–3 (FSD "characters 2–4") + yyMMddHHmmss of the business instant + ".RFA" — 19 characters, base 15
// (the Activity 27–29 positional rule). The business instant itself and the wait after a name clash are PrcFileNamePolicy.BusinessNow and
// .DelayToNextSecond, reused unchanged.
using System.Globalization;

namespace TransactHub.Activities.Wps.Inward.Rfa;

public static class RfaFileNamePolicy
{
    /// <summary>The extension, which <c>CK_RfaFiles_Name</c> requires (§7.1).</summary>
    public const string Extension = ".RFA";

    /// <summary>The name's length: 3 + 12 + 4.</summary>
    public const int NameLength = 19;

    /// <summary>The base name's length, without the extension (the Activity 27–29 rule).</summary>
    public const int BaseNameLength = 15;

    /// <summary>
    /// <c>{R}</c> of §6.2 and §6.3: characters 1–3 (zero-based) of the head routing code, e.g. <c>408</c> of <c>740810000</c>. The code must satisfy
    /// <see cref="InwardRfaParameters.IsValidHeadRoutingCode"/>.
    /// </summary>
    public static string RoutingPrefix(string headRoutingCode)
    {
        if (!InwardRfaParameters.IsValidHeadRoutingCode(headRoutingCode))
        {
            throw new ArgumentException("The head routing code must be 4–20 ASCII letters or digits.", nameof(headRoutingCode));
        }

        return headRoutingCode.Substring(1, 3);
    }

    /// <summary>The RFA name for the head routing code and the business instant (truncated to the second by the caller, §6.2).</summary>
    public static string NameFor(string headRoutingCode, DateTime businessInstant) =>
        RoutingPrefix(headRoutingCode) + businessInstant.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture) + Extension;
}
