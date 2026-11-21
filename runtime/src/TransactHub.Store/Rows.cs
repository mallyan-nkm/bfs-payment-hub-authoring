namespace TransactHub.Store;

// The generalized store (plan W2). Every table is insert-only: a row is written once and never updated
// or deleted (InsertOnlyGuard enforces it). Scheme and bank are columns, never schemas. JSON is kept as
// text, so the same columns work on SQL Server and PostgreSQL.

/// <summary>Marks a row the store writes once.</summary>
public interface IInsertOnly;

public sealed class AccountCols
{
    public string? Iban { get; set; }
    public string? OtherId { get; set; }
    public string? OtherScheme { get; set; }
    public string? Currency { get; set; }
    public string? Name { get; set; }
}

public sealed class AgentCols
{
    public string? Bicfi { get; set; }
    public string? Lei { get; set; }
    public string? ClearingSystem { get; set; }
    public string? MemberId { get; set; }
    public string? Name { get; set; }
}

/// <summary>A party (debtor, creditor, ultimate). Personal data lives here and nowhere else.</summary>
public sealed class PartyRow : IInsertOnly
{
    public string Id { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string? Name { get; set; }
    public bool IsOrganisation { get; set; }
    public string? Department { get; set; }
    public string? StreetName { get; set; }
    public string? BuildingNumber { get; set; }
    public string? PostCode { get; set; }
    public string? TownName { get; set; }
    public string? CountrySubDivision { get; set; }
    public string? Country { get; set; }
    public string AddressLinesJson { get; set; } = "[]";
    public string? AnyBic { get; set; }
    public string? Lei { get; set; }
    public string OtherIdsJson { get; set; } = "[]";
    public string? CountryOfResidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A bulk: pain.001 PaymentInformation.</summary>
public sealed class BulkRow : IInsertOnly
{
    public string Id { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string Scheme { get; set; } = "";
    public string Direction { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string PaymentInformationId { get; set; } = "";
    public DateTimeOffset CreationDateTime { get; set; }
    public DateOnly? RequestedExecutionDate { get; set; }
    public string DebtorPartyId { get; set; } = "";
    public AccountCols DebtorAccount { get; set; } = new();
    public AgentCols DebtorAgent { get; set; } = new();
    public string? CategoryPurposeCode { get; set; }
    public int NumberOfTransactions { get; set; }
    public decimal ControlSum { get; set; }
    public string SupplementaryJson { get; set; } = "{}";
    public string ReleaseId { get; set; } = "";
    public DateTimeOffset AcceptedAt { get; set; }
}

/// <summary>One credit transfer: pain.001 / pacs.008 CdtTrfTxInf.</summary>
public sealed class PaymentRow : IInsertOnly
{
    public string Id { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string Scheme { get; set; } = "";
    public string Direction { get; set; } = "";
    public string? BulkId { get; set; }
    public int LineNo { get; set; }
    public string? InstructionId { get; set; }
    public string EndToEndId { get; set; } = "";
    public string? TransactionId { get; set; }
    public string? Uetr { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public decimal? SettlementAmount { get; set; }
    public string? SettlementCurrency { get; set; }
    public DateOnly? SettlementDate { get; set; }
    public string ChargeBearer { get; set; } = "SLEV";
    public string? UltimateDebtorPartyId { get; set; }
    public string? DebtorPartyId { get; set; }
    public AccountCols DebtorAccount { get; set; } = new();
    public AgentCols DebtorAgent { get; set; } = new();
    public AgentCols CreditorAgent { get; set; } = new();
    public string CreditorPartyId { get; set; } = "";
    public AccountCols CreditorAccount { get; set; } = new();
    public string? UltimateCreditorPartyId { get; set; }
    public string? PurposeCode { get; set; }
    public string? CategoryPurposeCode { get; set; }
    public string RemittanceJson { get; set; } = "[]";
    public string SupplementaryJson { get; set; } = "{}";
    public string ReleaseId { get; set; } = "";
    public DateTimeOffset AcceptedAt { get; set; }
}

/// <summary>
/// A fact about a payment, a bulk or an exception item: a status, a step outcome, a posting, a message
/// sent. Facts are numbered per subject and chained by hash; the current status is the last status fact.
/// </summary>
public sealed class FactRow : IInsertOnly
{
    public long Id { get; set; }
    public string SubjectId { get; set; } = "";
    public string SubjectKind { get; set; } = "";
    public int Seq { get; set; }
    public string Kind { get; set; } = "";
    public string? Code { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTimeOffset At { get; set; }
    public string? ReleaseId { get; set; }
    public string Actor { get; set; } = "";
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

/// <summary>A message or file in or out, kept as received or sent, with its hash.</summary>
public sealed class MessageRow : IInsertOnly
{
    public string Id { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string Scheme { get; set; } = "";
    public string Direction { get; set; } = "";
    /// <summary>What it is: "pain.001", "pacs.002", "uae.wps.sif", "uae.wps.ack"…</summary>
    public string Kind { get; set; } = "";
    public string? FileName { get; set; }
    public byte[] Content { get; set; } = [];
    public string ContentHash { get; set; } = "";
    public string? SubjectId { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>An exception item for operations; what happens to it later is facts about it.</summary>
public sealed class ExceptionItemRow : IInsertOnly
{
    public string Id { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string? SubjectId { get; set; }
    public string Code { get; set; } = "";
    public string? Text { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTimeOffset RaisedAt { get; set; }
}

/// <summary>A released package from the studio, stored once.</summary>
public sealed class ReleaseRow : IInsertOnly
{
    public string Id { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Release { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string ManifestJson { get; set; } = "{}";
    public byte[] Package { get; set; } = [];
    public string StoredBy { get; set; } = "";
    public DateTimeOffset StoredAt { get; set; }
}

/// <summary>From when a release is the one new payments take, in an environment.</summary>
public sealed class ActivationRow : IInsertOnly
{
    public long Id { get; set; }
    public string Environment { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Release { get; set; } = "";
    public DateTimeOffset EffectiveFrom { get; set; }
    public string Actor { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>A published version of a rule, a list or a configuration value, effective-dated.</summary>
public sealed class VersionedValueRow : IInsertOnly
{
    public long Id { get; set; }
    /// <summary>"rule", "list" or "config".</summary>
    public string Kind { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string Name { get; set; } = "";
    public int VersionNo { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveTo { get; set; }
    public string BodyJson { get; set; } = "{}";
    public DateTimeOffset PublishedAt { get; set; }
    public string PublishedBy { get; set; } = "";
}

/// <summary>Outward facts for the report database and other readers, in the order they were written.</summary>
public sealed class OutboxRow : IInsertOnly
{
    public long Id { get; set; }
    public string Topic { get; set; } = "";
    public string Key { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset At { get; set; }
}
