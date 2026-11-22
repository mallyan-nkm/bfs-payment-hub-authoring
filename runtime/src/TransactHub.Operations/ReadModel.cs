using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

// The operations read model (R-05-48 to R-05-50): a projection of the store's facts, fed from the outbox. It is not the
// record: every row here can be thrown away and rebuilt from the facts (Projector.Rebuild). So, unlike the store, its
// rows are updated in place. It holds no personal data beyond what an operator needs, and that masked as the operations
// view declares. Same neutrality rules as the store: EF Core only, portable types, no provider-specific SQL.

/// <summary>Where the projection has got to in the outbox, and how far behind it is.</summary>
public sealed class OpsCheckpoint
{
    public string Name { get; set; } = "";
    /// <summary>Every outbox row up to here has been applied (or declared a gap that will never fill).</summary>
    public long Through { get; set; }
    public long Applied { get; set; }
    /// <summary>When the last applied fact was written to the store.</summary>
    public DateTimeOffset? LastFactAt { get; set; }
    /// <summary>When the projection last applied something.</summary>
    public DateTimeOffset? ProjectedAt { get; set; }
    public DateTimeOffset? RebuiltAt { get; set; }
}

/// <summary>How far each fact stream (a subject of the store's chain) has been applied: replay is idempotent by number.</summary>
public sealed class OpsStream
{
    public string StreamId { get; set; } = "";
    public int LastSeq { get; set; }
    /// <summary>For a payload reference: the payment or bulk it belongs to, once known.</summary>
    public string? SubjectId { get; set; }
    /// <summary>Staged payload writes not yet committed, only the paths the console reads (line facts).</summary>
    public string PendingJson { get; set; } = "{}";
}

/// <summary>A payment or a bulk, as an operator searches for it.</summary>
public sealed class OpsSubject
{
    public string Id { get; set; } = "";
    /// <summary>"bulk" or "payment".</summary>
    public string Kind { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string Scheme { get; set; } = "";
    public string Direction { get; set; } = "";
    public string? BulkId { get; set; }
    public string ReleaseId { get; set; } = "";
    public string? MessageId { get; set; }
    public string? EndToEndId { get; set; }
    public string? FileName { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public int Count { get; set; }
    /// <summary>The ISO status the workflow state last carried (ACCP, RJCT…), or the first status fact (RCVD).</summary>
    public string? Status { get; set; }
    /// <summary>The last step's outcome on the payment's own flow.</summary>
    public string? LastOutcome { get; set; }
    public string? LastStep { get; set; }
    /// <summary>"open" when a hold waits for a person.</summary>
    public string? Hold { get; set; }
    public string? HoldReason { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>The operations view the fields come from (name@release).</summary>
    public string? View { get; set; }
    /// <summary>The platform view's field values, masked: { fieldId: value }.</summary>
    public string FieldsJson { get; set; } = "{}";
}

/// <summary>A leg of a payment: one engine instance that a leg of the scheme's view matched.</summary>
public sealed class OpsLeg
{
    public string Id { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public string Instance { get; set; } = "";
    public string Scheme { get; set; } = "";
    /// <summary>The leg workflow the view matched (e.g. uae.wps.wf.salary-file-out.v1).</summary>
    public string Leg { get; set; } = "";
    public string Title { get; set; } = "";
    public string Direction { get; set; } = "";
    public string? LastStep { get; set; }
    public string? LastOutcome { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string FieldsJson { get; set; } = "{}";
}

/// <summary>One declared field's value, for filtering on it: text and, where it is a number, the number.</summary>
public sealed class OpsFieldValue
{
    public long Id { get; set; }
    public string SubjectId { get; set; } = "";
    /// <summary>The field's qualified id: "&lt;view&gt;:&lt;leg or payment&gt;:&lt;field&gt;".</summary>
    public string Field { get; set; } = "";
    public string? Text { get; set; }
    public decimal? Number { get; set; }
}

/// <summary>One entry of a payment's timeline: a fact of its chain, in its words, with no payload.</summary>
public sealed class OpsTimelineEntry
{
    public long Id { get; set; }
    public string SubjectId { get; set; } = "";
    public string StreamId { get; set; } = "";
    public int Seq { get; set; }
    public DateTimeOffset At { get; set; }
    public string Kind { get; set; } = "";
    public string? Code { get; set; }
    public string? Instance { get; set; }
    public string? Step { get; set; }
    public string? Activity { get; set; }
    public string Actor { get; set; } = "";
    /// <summary>The fact's data, without any payload: a step's ids, outcome and state writes; a hold's or an action's.</summary>
    public string DataJson { get; set; } = "{}";
}

/// <summary>A file or message in or out, by reference: its content stays in the store.</summary>
public sealed class OpsDocument
{
    public string Id { get; set; } = "";
    public string? SubjectId { get; set; }
    public string Tenant { get; set; } = "";
    public string Scheme { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Direction { get; set; } = "";
    public string? FileName { get; set; }
    public int Size { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

/// <summary>A line of a bulk (a salary line): its declared fields, masked, and its own facts from the payload.</summary>
public sealed class OpsLine
{
    public string Id { get; set; } = "";
    public string BulkId { get; set; } = "";
    public int LineNo { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    /// <summary>The line's facts (items[i].facts) as the payload holds them: validation results, not personal data.</summary>
    public string FactsJson { get; set; } = "{}";
    public string FieldsJson { get; set; } = "{}";
}

/// <summary>A hold: a payment parked for a person.</summary>
public sealed class OpsHold
{
    public string Id { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public string Tenant { get; set; } = "";
    /// <summary>"approval" or "repair".</summary>
    public string Kind { get; set; } = "";
    public string Activity { get; set; } = "";
    public string? Reason { get; set; }
    /// <summary>"open", "decided" or "expired".</summary>
    public string Status { get; set; } = "";
    public string? Decision { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
}

/// <summary>An operator action, as its facts tell it: requested, approved by a second person where needed, carried out or refused.</summary>
public sealed class OpsAction
{
    public string Id { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string Action { get; set; } = "";
    public string? HoldId { get; set; }
    /// <summary>"pending-approval", "done", "rejected" or "refused".</summary>
    public string Status { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public string? Remarks { get; set; }
    /// <summary>The role a second approver needs, when the view asks for one.</summary>
    public string? SecondApprover { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? Result { get; set; }
}

public sealed class OpsDbContext(DbContextOptions<OpsDbContext> options) : DbContext(options)
{
    public const string HistoryTable = "__ops_migrations";
    public DbSet<OpsCheckpoint> Checkpoints => Set<OpsCheckpoint>();
    public DbSet<OpsStream> Streams => Set<OpsStream>();
    public DbSet<OpsSubject> Subjects => Set<OpsSubject>();
    public DbSet<OpsLeg> Legs => Set<OpsLeg>();
    public DbSet<OpsFieldValue> FieldValues => Set<OpsFieldValue>();
    public DbSet<OpsTimelineEntry> Timeline => Set<OpsTimelineEntry>();
    public DbSet<OpsDocument> Documents => Set<OpsDocument>();
    public DbSet<OpsLine> Lines => Set<OpsLine>();
    public DbSet<OpsHold> Holds => Set<OpsHold>();
    public DbSet<OpsAction> Actions => Set<OpsAction>();

    /// <summary>Each provider has its own migrations, generated from this one model.</summary>
    public static string MigrationsAssembly(StoreProvider p) => $"TransactHub.Operations.Migrations.{p}";

    /// <summary>The read model on the given database: the store's, or one of its own.</summary>
    public static OpsDbContext Open(StoreOptions o) => new(StoreDbContext.ConfigureFor<OpsDbContext>(o, MigrationsAssembly(o.Provider), HistoryTable));

    protected override void OnModelCreating(ModelBuilder m)
    {
        const int Id = 64, Code = 64, Short = 140;
        m.Entity<OpsCheckpoint>(e =>
        {
            e.ToTable("ops_checkpoint");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(Code);
        });
        m.Entity<OpsStream>(e =>
        {
            e.ToTable("ops_stream");
            e.HasKey(x => x.StreamId);
            e.Property(x => x.StreamId).HasMaxLength(Id);
            e.Property(x => x.SubjectId).HasMaxLength(Id);
            e.HasIndex(x => x.SubjectId);
        });
        m.Entity<OpsSubject>(e =>
        {
            e.ToTable("ops_subject");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Id);
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Tenant).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Scheme).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Direction).HasMaxLength(16).IsRequired();
            e.Property(x => x.BulkId).HasMaxLength(Id);
            e.Property(x => x.ReleaseId).HasMaxLength(Short).IsRequired();
            e.Property(x => x.MessageId).HasMaxLength(35);
            e.Property(x => x.EndToEndId).HasMaxLength(35);
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.Amount).HasPrecision(19, 4);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.LastOutcome).HasMaxLength(Code);
            e.Property(x => x.LastStep).HasMaxLength(Short);
            e.Property(x => x.Hold).HasMaxLength(16);
            e.Property(x => x.HoldReason).HasMaxLength(Short);
            e.Property(x => x.View).HasMaxLength(Short);
            e.HasIndex(x => new { x.Tenant, x.Scheme, x.AcceptedAt });
            e.HasIndex(x => new { x.Tenant, x.Hold });
            e.HasIndex(x => x.MessageId);
            e.HasIndex(x => x.EndToEndId);
        });
        m.Entity<OpsLeg>(e =>
        {
            e.ToTable("ops_leg");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(200);
            e.Property(x => x.SubjectId).HasMaxLength(Id).IsRequired();
            e.Property(x => x.Instance).HasMaxLength(Short).IsRequired();
            e.Property(x => x.Scheme).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Leg).HasMaxLength(Short).IsRequired();
            e.Property(x => x.Title).HasMaxLength(Short);
            e.Property(x => x.Direction).HasMaxLength(16);
            e.Property(x => x.LastStep).HasMaxLength(Short);
            e.Property(x => x.LastOutcome).HasMaxLength(Code);
            e.HasIndex(x => x.SubjectId);
            e.HasIndex(x => new { x.Leg, x.LastOutcome });
        });
        m.Entity<OpsFieldValue>(e =>
        {
            e.ToTable("ops_field_value");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.SubjectId).HasMaxLength(Id).IsRequired();
            e.Property(x => x.Field).HasMaxLength(200).IsRequired();
            e.Property(x => x.Text).HasMaxLength(400);
            e.Property(x => x.Number).HasPrecision(19, 4);
            e.HasIndex(x => new { x.Field, x.Text });
            e.HasIndex(x => new { x.Field, x.Number });
            e.HasIndex(x => x.SubjectId);
        });
        m.Entity<OpsTimelineEntry>(e =>
        {
            e.ToTable("ops_timeline");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.SubjectId).HasMaxLength(Id).IsRequired();
            e.Property(x => x.StreamId).HasMaxLength(Id).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Code).HasMaxLength(Code);
            e.Property(x => x.Instance).HasMaxLength(Short);
            e.Property(x => x.Step).HasMaxLength(Short);
            e.Property(x => x.Activity).HasMaxLength(Short);
            e.Property(x => x.Actor).HasMaxLength(Short).IsRequired();
            e.HasIndex(x => new { x.StreamId, x.Seq }).IsUnique();
            e.HasIndex(x => new { x.SubjectId, x.At });
        });
        m.Entity<OpsDocument>(e =>
        {
            e.ToTable("ops_document");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Id);
            e.Property(x => x.SubjectId).HasMaxLength(Id);
            e.Property(x => x.Tenant).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Scheme).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Direction).HasMaxLength(16).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.SubjectId);
            e.HasIndex(x => x.FileName);
        });
        m.Entity<OpsLine>(e =>
        {
            e.ToTable("ops_line");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Id);
            e.Property(x => x.BulkId).HasMaxLength(Id).IsRequired();
            e.Property(x => x.Amount).HasPrecision(19, 4);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => new { x.BulkId, x.LineNo });
        });
        m.Entity<OpsHold>(e =>
        {
            e.ToTable("ops_hold");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Id);
            e.Property(x => x.SubjectId).HasMaxLength(Id).IsRequired();
            e.Property(x => x.Tenant).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Activity).HasMaxLength(Short).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(Short);
            e.Property(x => x.Status).HasMaxLength(16).IsRequired();
            e.Property(x => x.Decision).HasMaxLength(Code);
            e.Property(x => x.DecidedBy).HasMaxLength(Short);
            e.HasIndex(x => new { x.Tenant, x.Status });
            e.HasIndex(x => x.SubjectId);
        });
        m.Entity<OpsAction>(e =>
        {
            e.ToTable("ops_action");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Id);
            e.Property(x => x.SubjectId).HasMaxLength(Id).IsRequired();
            e.Property(x => x.Tenant).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Action).HasMaxLength(Code).IsRequired();
            e.Property(x => x.HoldId).HasMaxLength(Id);
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.Property(x => x.RequestedBy).HasMaxLength(Short).IsRequired();
            e.Property(x => x.Remarks).HasMaxLength(1000);
            e.Property(x => x.SecondApprover).HasMaxLength(Code);
            e.Property(x => x.ApprovedBy).HasMaxLength(Short);
            e.Property(x => x.Result).HasMaxLength(400);
            e.HasIndex(x => new { x.Tenant, x.Status });
            e.HasIndex(x => x.SubjectId);
        });
    }
}
