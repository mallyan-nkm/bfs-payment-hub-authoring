namespace TransactHub.Store;

// The bank's registers (R-05-62 to R-05-64; owner's decisions of 23 November 2026): bank customer data the runtime keeps and
// the operations console maintains, starting with the corporate registration. Never sent to the studio or to GitHub. Every row
// is insert-only: a change is a maker's draft (a change and its versions), and it takes effect only when a decision row from a
// different person with the bank approver's role approves it. History is every version, approved, rejected or waiting.

/// <summary>A maker's change to a register: one or more records, from the console, the register API or a bulk sync.</summary>
public sealed class RegisterChangeRow : IInsertOnly
{
    public string Id { get; set; } = "";
    public string Tenant { get; set; } = "";
    /// <summary>The register kind, for example "corporate".</summary>
    public string Kind { get; set; } = "";
    /// <summary>"console", "api" or "sync".</summary>
    public string Source { get; set; } = "";
    public string MadeBy { get; set; } = "";
    public DateTimeOffset MadeAt { get; set; }
    public string? Remarks { get; set; }
    /// <summary>How many versions the change holds.</summary>
    public int Count { get; set; }
    /// <summary>The caller's own reference, for a sync: idempotent by it.</summary>
    public string? Reference { get; set; }
}

/// <summary>One record of a register as a change gives it: the whole record, from its validFrom, numbered per key.</summary>
public sealed class RegisterVersionRow : IInsertOnly
{
    public long Id { get; set; }
    public string ChangeId { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string Kind { get; set; } = "";
    /// <summary>The record's key, for a corporate its corporate id.</summary>
    public string Key { get; set; } = "";
    public int VersionNo { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    /// <summary>The record's fields, as the register kind declares them (JSON).</summary>
    public string DataJson { get; set; } = "{}";
    /// <summary>The SHA-256 of the canonical record, so an unchanged record is not drafted again.</summary>
    public string Hash { get; set; } = "";
}

/// <summary>A value of a field the register kind looks records up by (a CIF, an establishment id, an account), per version.</summary>
public sealed class RegisterIndexRow : IInsertOnly
{
    public long Id { get; set; }
    public long VersionId { get; set; }
    public string Tenant { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Field { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>The bank approver's consent to a change, or its refusal: one per change, by someone other than its maker.</summary>
public sealed class RegisterDecisionRow : IInsertOnly
{
    public long Id { get; set; }
    public string ChangeId { get; set; } = "";
    /// <summary>"approved" or "rejected".</summary>
    public string Decision { get; set; } = "";
    public string DecidedBy { get; set; } = "";
    public DateTimeOffset DecidedAt { get; set; }
    public string? Remarks { get; set; }
}
