using System.Text.Json.Nodes;
using TransactHub.Activities.Native;

namespace TransactHub.Activities.Platform.Native;

/// <summary>
/// <c>p.enrich.party-register.v1</c> on the runtime (R-12-27; owner's decisions of 23 November 2026): the debtor looked up in the
/// bank's party register through the tenant's binding of <c>p.port.party-register.v1</c> (the runtime's own register, as of the
/// step), by its organisation id (<c>batch.debtor.organisationId.id</c>) matched against the register's field <c>lookupBy</c>
/// names. The platform's match: found and active, and the supplied debit account among the registered accounts. A scheme may give
/// its own match (keyed by <c>header.scheme</c>), contributed by its module in the same distribution as a <see cref="PartyMatcher"/>
/// under <c>party-matcher:&lt;scheme&gt;</c>, which then decides. Only the answer is written to <c>facts.partyRegister</c>: found,
/// active and the scheme's code; the record itself stays here.
/// </summary>
public sealed class EnrichPartyRegister(INativePorts ports, Func<string, object?> contributed) : INativeActivity
{
    public const string Port = "p.port.party-register.v1";
    public string Name => "p.enrich.party-register.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var kind = NativeContext.Param(a.Params, "register") is { Length: > 0 } k ? k : "corporate";
        var field = NativeContext.Param(a.Params, "lookupBy") is { Length: > 0 } f ? f : "establishmentId";
        var id = NativeContext.S(a.View, "batch.debtor.organisationId.id");
        NativeParty? party = null;
        // The binding is resolved even for a debtor with no id: a tenant with none bound is refused, as before.
        var answer = ports.Ask(a, Port, "lookup", new JsonObject { ["kind"] = kind, ["field"] = field, ["value"] = id.Length > 0 ? id : null });
        if (id.Length > 0 && answer.Body["found"]?.GetValue<bool>() == true)
            party = new NativeParty((string?)answer.Body["key"] ?? "", answer.Body["record"] as JsonObject ?? new JsonObject());
        var active = party is not null && (bool?)party.Record["active"] != false;
        PartyMatch? miss;
        var scheme = NativeContext.S(a.View, "header.scheme");
        if (contributed($"party-matcher:{scheme}") is PartyMatcher m) miss = m(id, party, a.View);
        else miss = Platform(id, party, active, a.View);
        var fact = new JsonObject { ["register"] = kind, ["lookupBy"] = field, ["found"] = party is not null, ["active"] = active };
        if (miss is not null)
        {
            // The code only: a scheme's message may name the id asked, which the state does not carry in clear.
            fact["schemeCode"] = miss.SchemeCode;
            if (miss.Outcome == "mismatch") fact["mismatch"] = "details";
        }
        return new NativeResult(miss?.Outcome ?? "registered", miss?.ReasonCode, new JsonObject { ["facts.partyRegister"] = fact });
    }

    /// <summary>The platform's own match, for a scheme that gives none.</summary>
    static PartyMatch? Platform(string id, NativeParty? party, bool active, JsonObject view)
    {
        if (id.Length == 0) return new PartyMatch("not-registered", "RR04", "NO-ID", "The debtor carries no organisation id to look up");
        if (party is null || !active) return new PartyMatch("not-registered", "RR04", "NOT-REGISTERED", $"The debtor {Mask(id)} is not an active registered party");
        var account = NativeContext.S(view, "batch.debtorAccount.iban") is { Length: > 0 } iban ? iban : NativeContext.S(view, "batch.debtorAccount.other");
        if (account.Length > 0 && party.Record["accounts"] is JsonArray accts && accts.Count > 0 && !accts.Any(x => string.Equals((string?)x, account, StringComparison.OrdinalIgnoreCase)))
            return new PartyMatch("mismatch", "BE01", "ACCOUNT", "The supplied debit account is not one the party registered");
        return null;
    }

    static string Mask(string id) => id.Length <= 4 ? "****" : id[..2] + new string('*', id.Length - 4) + id[^2..];
}
