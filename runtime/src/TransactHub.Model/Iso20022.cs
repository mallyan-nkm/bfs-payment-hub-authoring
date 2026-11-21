using System.Text.Json.Nodes;

namespace TransactHub.Model;

// The internal payment model (plan W1). It follows ISO 20022: the element names and their meaning are
// those of pain.001 (customer credit transfer initiation) and pacs.008 (FI to FI customer credit
// transfer), narrowed to what payments here use. A scheme's fields that have no ISO element go into
// SupplementaryData, keyed by the scheme, never into new properties.

/// <summary>ActiveOrHistoricCurrencyAndAmount: an amount and its ISO 4217 currency.</summary>
public sealed record Money(decimal Amount, string Currency)
{
    public static Money Aed(decimal amount) => new(amount, "AED");
    public override string ToString() => $"{Currency} {Amount:0.00}";
}

/// <summary>PostalAddress24, structured. AddressLine is kept only for sources that send no structure.</summary>
public sealed record PostalAddress
{
    public string? Department { get; init; }
    public string? StreetName { get; init; }
    public string? BuildingNumber { get; init; }
    public string? PostCode { get; init; }
    public string? TownName { get; init; }
    public string? CountrySubDivision { get; init; }
    /// <summary>ISO 3166 alpha-2.</summary>
    public string? Country { get; init; }
    public IReadOnlyList<string> AddressLine { get; init; } = [];
}

/// <summary>GenericIdentification: an identifier with its scheme (e.g. scheme "MOL" for a UAE establishment id).</summary>
public sealed record GenericId(string Id, string? SchemeName = null, string? Issuer = null);

/// <summary>PartyIdentification135: a debtor, creditor or ultimate party.</summary>
public sealed record Party
{
    public string? Name { get; init; }
    public PostalAddress? PostalAddress { get; init; }
    /// <summary>OrganisationIdentification: AnyBIC.</summary>
    public string? AnyBic { get; init; }
    /// <summary>OrganisationIdentification: LEI.</summary>
    public string? Lei { get; init; }
    /// <summary>OrganisationIdentification or PrivateIdentification: Other.</summary>
    public IReadOnlyList<GenericId> OtherIds { get; init; } = [];
    public bool IsOrganisation { get; init; }
    /// <summary>ISO 3166 alpha-2.</summary>
    public string? CountryOfResidence { get; init; }
}

/// <summary>CashAccount40: an IBAN or another identification.</summary>
public sealed record Account
{
    public string? Iban { get; init; }
    public GenericId? Other { get; init; }
    public string? Currency { get; init; }
    public string? Name { get; init; }
    public string Display => Iban ?? Other?.Id ?? "";
}

/// <summary>BranchAndFinancialInstitutionIdentification6: an agent.</summary>
public sealed record Agent
{
    public string? Bicfi { get; init; }
    public string? Lei { get; init; }
    /// <summary>ClearingSystemMemberIdentification: the clearing system (e.g. "AEUAE" for UAE routing codes) and the member id.</summary>
    public string? ClearingSystem { get; init; }
    public string? MemberId { get; init; }
    public string? Name { get; init; }
}

/// <summary>PaymentIdentification13.</summary>
public sealed record PaymentIdentification
{
    public string? InstructionId { get; init; }
    public required string EndToEndId { get; init; }
    public string? TransactionId { get; init; }
    /// <summary>UETR, a UUID v4.</summary>
    public string? Uetr { get; init; }
}

/// <summary>ChargeBearerType1Code.</summary>
public enum ChargeBearer { DEBT, CRED, SHAR, SLEV }

/// <summary>
/// One credit transfer transaction: CdtTrfTxInf of pain.001 or pacs.008. The amounts follow pacs.008:
/// InstructedAmount is what the debtor asked for; InterbankSettlementAmount is what settles between
/// agents, set when it is known.
/// </summary>
public sealed record CreditTransfer
{
    public required PaymentIdentification PaymentId { get; init; }
    public required Money InstructedAmount { get; init; }
    public Money? InterbankSettlementAmount { get; init; }
    public DateOnly? InterbankSettlementDate { get; init; }
    public ChargeBearer ChargeBearer { get; init; } = ChargeBearer.SLEV;
    public Party? UltimateDebtor { get; init; }
    public Party? Debtor { get; init; }
    public Account? DebtorAccount { get; init; }
    public Agent? DebtorAgent { get; init; }
    public Agent? CreditorAgent { get; init; }
    public required Party Creditor { get; init; }
    public required Account CreditorAccount { get; init; }
    public Party? UltimateCreditor { get; init; }
    /// <summary>Purpose: an ExternalPurpose1Code (e.g. "SALA" for salary).</summary>
    public string? PurposeCode { get; init; }
    public string? CategoryPurposeCode { get; init; }
    public IReadOnlyList<string> RemittanceUnstructured { get; init; } = [];
    /// <summary>SupplementaryData, keyed by scheme scope (e.g. "uae.wps"), each declared by the scheme's message schema.</summary>
    public IReadOnlyDictionary<string, JsonObject> SupplementaryData { get; init; } = new Dictionary<string, JsonObject>();
}

/// <summary>
/// A bulk: the PaymentInformation block of pain.001 (one debtor, one account, one execution date, many
/// transactions). A WPS salary file is one bulk; each employee line is one transaction.
/// </summary>
public sealed record PaymentBulk
{
    /// <summary>GroupHeader MessageIdentification.</summary>
    public required string MessageId { get; init; }
    /// <summary>PaymentInformationIdentification.</summary>
    public required string PaymentInformationId { get; init; }
    public DateTimeOffset CreationDateTime { get; init; }
    public DateOnly? RequestedExecutionDate { get; init; }
    public required Party Debtor { get; init; }
    public required Account DebtorAccount { get; init; }
    public Agent? DebtorAgent { get; init; }
    public string? CategoryPurposeCode { get; init; }
    public IReadOnlyList<CreditTransfer> Transactions { get; init; } = [];
    public IReadOnlyDictionary<string, JsonObject> SupplementaryData { get; init; } = new Dictionary<string, JsonObject>();

    /// <summary>NumberOfTransactions, computed.</summary>
    public int NumberOfTransactions => Transactions.Count;
    /// <summary>ControlSum, computed over the instructed amounts.</summary>
    public decimal ControlSum => Transactions.Sum(t => t.InstructedAmount.Amount);
}

/// <summary>ExternalPaymentTransactionStatus1Code, as pacs.002 and pain.002 report it.</summary>
public enum TransactionStatus
{
    /// <summary>Received.</summary>
    RCVD,
    /// <summary>Accepted technical validation.</summary>
    ACTC,
    /// <summary>Accepted customer profile.</summary>
    ACCP,
    /// <summary>Accepted settlement in process.</summary>
    ACSP,
    /// <summary>Accepted settlement completed (debtor side).</summary>
    ACSC,
    /// <summary>Accepted credit settlement completed.</summary>
    ACCC,
    /// <summary>Pending.</summary>
    PDNG,
    /// <summary>Rejected.</summary>
    RJCT,
    /// <summary>Accepted with change.</summary>
    ACWC,
    /// <summary>Partially accepted (a bulk).</summary>
    PART,
}

/// <summary>A status report line: pacs.002 TxInfAndSts or OrgnlPmtInfAndSts, with an ISO reason code and the scheme's own code.</summary>
public sealed record StatusReport(TransactionStatus Status, string? IsoReason = null, string? SchemeCode = null, string? Text = null);
