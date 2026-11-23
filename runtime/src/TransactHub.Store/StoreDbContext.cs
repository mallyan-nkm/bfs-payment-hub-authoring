using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TransactHub.Store;

public enum StoreProvider { SqlServer, PostgreSql }

public sealed record StoreOptions(StoreProvider Provider, string ConnectionString);

/// <summary>
/// The one model, on either database. Nothing here is provider-specific but the two lines in
/// <see cref="Configure"/>: column types are the portable ones, JSON is text, ids come from the
/// application, times are UTC.
/// </summary>
public sealed class StoreDbContext(DbContextOptions<StoreDbContext> options) : DbContext(options)
{
    public DbSet<PartyRow> Parties => Set<PartyRow>();
    public DbSet<BulkRow> Bulks => Set<BulkRow>();
    public DbSet<PaymentRow> Payments => Set<PaymentRow>();
    public DbSet<FactRow> Facts => Set<FactRow>();
    public DbSet<MessageRow> Messages => Set<MessageRow>();
    public DbSet<StoredFileRow> StoredFiles => Set<StoredFileRow>();
    public DbSet<FileContentRow> FileContents => Set<FileContentRow>();
    public DbSet<ExceptionItemRow> ExceptionItems => Set<ExceptionItemRow>();
    public DbSet<ReleaseRow> Releases => Set<ReleaseRow>();
    public DbSet<ActivationRow> Activations => Set<ActivationRow>();
    public DbSet<VersionedValueRow> VersionedValues => Set<VersionedValueRow>();
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();

    /// <summary>The only place a provider is named (the build check allows it here).</summary>
    public static DbContextOptions<StoreDbContext> Configure(StoreOptions o)
    {
        var b = new DbContextOptionsBuilder<StoreDbContext>();
        _ = o.Provider switch
        {
            StoreProvider.SqlServer => b.UseSqlServer(o.ConnectionString, x => x.MigrationsAssembly(MigrationsAssembly(o.Provider))),
            StoreProvider.PostgreSql => b.UseNpgsql(o.ConnectionString, x => x.MigrationsAssembly(MigrationsAssembly(o.Provider))),
            _ => throw new ArgumentOutOfRangeException(nameof(o)),
        };
        b.AddInterceptors(new InsertOnlyGuard(), new StoreWriteTelemetry(o.Provider));
        return b.Options;
    }

    public static StoreDbContext Open(StoreOptions o) => new(Configure(o));

    /// <summary>
    /// The same provider choice for another of the runtime's contexts (the operations read model): its own
    /// migrations assembly and history table, nothing else provider-specific.
    /// </summary>
    public static DbContextOptions<T> ConfigureFor<T>(StoreOptions o, string migrationsAssembly, string historyTable) where T : DbContext
    {
        var b = new DbContextOptionsBuilder<T>();
        _ = o.Provider switch
        {
            StoreProvider.SqlServer => b.UseSqlServer(o.ConnectionString, x => x.MigrationsAssembly(migrationsAssembly).MigrationsHistoryTable(historyTable)),
            StoreProvider.PostgreSql => b.UseNpgsql(o.ConnectionString, x => x.MigrationsAssembly(migrationsAssembly).MigrationsHistoryTable(historyTable)),
            _ => throw new ArgumentOutOfRangeException(nameof(o)),
        };
        return b.Options;
    }

    /// <summary>Each provider has its own migrations, generated from this one model.</summary>
    public static string MigrationsAssembly(StoreProvider p) => $"TransactHub.Store.Migrations.{p}";

    protected override void OnModelCreating(ModelBuilder m)
    {
        const int Id = 64, Code = 64, Short = 140, Ccy = 3;

        m.Entity<PartyRow>(e =>
        {
            e.ToTable("party");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Id);
            e.Property(x => x.Tenant).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Name).HasMaxLength(Short);
            e.Property(x => x.Department).HasMaxLength(70);
            e.Property(x => x.StreetName).HasMaxLength(70);
            e.Property(x => x.BuildingNumber).HasMaxLength(16);
            e.Property(x => x.PostCode).HasMaxLength(16);
            e.Property(x => x.TownName).HasMaxLength(35);
            e.Property(x => x.CountrySubDivision).HasMaxLength(35);
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.AnyBic).HasMaxLength(11);
            e.Property(x => x.Lei).HasMaxLength(20);
            e.Property(x => x.CountryOfResidence).HasMaxLength(2);
        });

        m.Entity<BulkRow>(e =>
        {
            e.ToTable("payment_bulk");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Id);
            e.Property(x => x.Tenant).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Scheme).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Direction).HasMaxLength(16).IsRequired();
            e.Property(x => x.MessageId).HasMaxLength(35).IsRequired();
            e.Property(x => x.PaymentInformationId).HasMaxLength(35).IsRequired();
            e.Property(x => x.DebtorPartyId).HasMaxLength(Id);
            e.Property(x => x.InitiatingPartyId).HasMaxLength(Id);
            e.Property(x => x.CategoryPurposeCode).HasMaxLength(4);
            e.Property(x => x.ControlSum).HasPrecision(19, 4);
            e.Property(x => x.ReleaseId).HasMaxLength(Short).IsRequired();
            e.OwnsOne(x => x.DebtorAccount, a => Account(a, "debtor_account"));
            e.OwnsOne(x => x.DebtorAgent, a => Agent(a, "debtor_agent"));
            e.HasIndex(x => new { x.Tenant, x.Scheme, x.MessageId });
        });

        m.Entity<PaymentRow>(e =>
        {
            e.ToTable("payment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Id);
            e.Property(x => x.Tenant).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Scheme).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Direction).HasMaxLength(16).IsRequired();
            e.Property(x => x.BulkId).HasMaxLength(Id);
            e.Property(x => x.InstructionId).HasMaxLength(35);
            e.Property(x => x.EndToEndId).HasMaxLength(35).IsRequired();
            e.Property(x => x.TransactionId).HasMaxLength(35);
            e.Property(x => x.Uetr).HasMaxLength(36);
            e.Property(x => x.Amount).HasPrecision(19, 4);
            e.Property(x => x.Currency).HasMaxLength(Ccy).IsRequired();
            e.Property(x => x.SettlementAmount).HasPrecision(19, 4);
            e.Property(x => x.SettlementCurrency).HasMaxLength(Ccy);
            e.Property(x => x.ChargeBearer).HasMaxLength(4);
            e.Property(x => x.UltimateDebtorPartyId).HasMaxLength(Id);
            e.Property(x => x.DebtorPartyId).HasMaxLength(Id);
            e.Property(x => x.CreditorPartyId).HasMaxLength(Id).IsRequired();
            e.Property(x => x.UltimateCreditorPartyId).HasMaxLength(Id);
            e.Property(x => x.PurposeCode).HasMaxLength(4);
            e.Property(x => x.CategoryPurposeCode).HasMaxLength(4);
            e.Property(x => x.ReleaseId).HasMaxLength(Short).IsRequired();
            e.OwnsOne(x => x.DebtorAccount, a => Account(a, "debtor_account"));
            e.OwnsOne(x => x.DebtorAgent, a => Agent(a, "debtor_agent"));
            e.OwnsOne(x => x.CreditorAgent, a => Agent(a, "creditor_agent"));
            e.OwnsOne(x => x.CreditorAccount, a => Account(a, "creditor_account"));
            e.HasIndex(x => new { x.BulkId, x.LineNo });
            e.HasIndex(x => new { x.Tenant, x.Scheme, x.EndToEndId });
            e.HasIndex(x => x.Uetr);
        });

        m.Entity<FactRow>(e =>
        {
            e.ToTable("payment_fact");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.SubjectId).HasMaxLength(Id).IsRequired();
            e.Property(x => x.SubjectKind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Code).HasMaxLength(Code);
            e.Property(x => x.ReleaseId).HasMaxLength(Short);
            e.Property(x => x.Actor).HasMaxLength(Short).IsRequired();
            e.Property(x => x.PrevHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Hash).HasMaxLength(64).IsRequired();
            // Added nullable (expand): a host of an earlier release writes facts without them and reads past them (R-09-69).
            e.Property(x => x.TraceId).HasMaxLength(32);
            e.Property(x => x.SpanId).HasMaxLength(16);
            e.HasIndex(x => x.TraceId);
            // One numbering per subject: two writers racing for the same number cannot both win.
            e.HasIndex(x => new { x.SubjectId, x.Seq }).IsUnique();
            e.HasIndex(x => new { x.Kind, x.Code });
        });

        m.Entity<MessageRow>(e =>
        {
            e.ToTable("message");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Id);
            e.Property(x => x.Tenant).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Scheme).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Direction).HasMaxLength(16).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(Code).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.SubjectId).HasMaxLength(Id);
            e.HasIndex(x => new { x.Tenant, x.Scheme, x.Kind, x.ContentHash });
            e.HasIndex(x => new { x.Tenant, x.Scheme, x.Kind, x.FileName });
        });

        m.Entity<StoredFileRow>(e =>
        {
            e.ToTable("stored_file");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Id);
            e.Property(x => x.Tenant).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Scheme).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Direction).HasMaxLength(16).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(Short).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Signature).HasMaxLength(256).IsRequired();
            e.Property(x => x.SigningKeyId).HasMaxLength(Id).IsRequired();
            e.Property(x => x.SubjectId).HasMaxLength(Id);
            e.Property(x => x.Leg).HasMaxLength(200);
            e.Property(x => x.Activity).HasMaxLength(Short);
            e.HasIndex(x => new { x.Tenant, x.SubjectId });
            e.HasIndex(x => new { x.Tenant, x.ContentHash });
        });

        m.Entity<FileContentRow>(e =>
        {
            e.ToTable("file_content");
            e.HasKey(x => x.ContentHash);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.Content).IsRequired();
        });

        m.Entity<ExceptionItemRow>(e =>
        {
            e.ToTable("exception_item");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Id);
            e.Property(x => x.Tenant).HasMaxLength(Code).IsRequired();
            e.Property(x => x.SubjectId).HasMaxLength(Id);
            e.Property(x => x.Code).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Text).HasMaxLength(1000);
            e.HasIndex(x => new { x.Tenant, x.Code });
        });

        m.Entity<ReleaseRow>(e =>
        {
            e.ToTable("release");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(Short);
            e.Property(x => x.Scope).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Release).HasMaxLength(32).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.StoredBy).HasMaxLength(Short).IsRequired();
            e.HasIndex(x => new { x.Scope, x.Release }).IsUnique();
        });

        m.Entity<ActivationRow>(e =>
        {
            e.ToTable("release_activation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Environment).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Scope).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Release).HasMaxLength(32).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(Short).IsRequired();
            e.HasIndex(x => new { x.Environment, x.Scope, x.EffectiveFrom });
        });

        m.Entity<VersionedValueRow>(e =>
        {
            e.ToTable("versioned_value");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Tenant).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.PublishedBy).HasMaxLength(Short).IsRequired();
            e.HasIndex(x => new { x.Kind, x.Tenant, x.Name, x.VersionNo }).IsUnique();
        });

        m.Entity<OutboxRow>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Topic).HasMaxLength(Code).IsRequired();
            e.Property(x => x.Key).HasMaxLength(Short).IsRequired();
        });
    }

    static void Account<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.OwnedNavigationBuilder<T, AccountCols> a, string prefix) where T : class
    {
        a.Property(x => x.Iban).HasColumnName($"{prefix}_iban").HasMaxLength(34);
        a.Property(x => x.OtherId).HasColumnName($"{prefix}_other_id").HasMaxLength(34);
        a.Property(x => x.OtherScheme).HasColumnName($"{prefix}_other_scheme").HasMaxLength(35);
        a.Property(x => x.Currency).HasColumnName($"{prefix}_currency").HasMaxLength(3);
        a.Property(x => x.Name).HasColumnName($"{prefix}_name").HasMaxLength(70);
        a.Property(x => x.Type).HasColumnName($"{prefix}_type").HasMaxLength(35);
    }

    static void Agent<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.OwnedNavigationBuilder<T, AgentCols> a, string prefix) where T : class
    {
        a.Property(x => x.Bicfi).HasColumnName($"{prefix}_bicfi").HasMaxLength(11);
        a.Property(x => x.Lei).HasColumnName($"{prefix}_lei").HasMaxLength(20);
        a.Property(x => x.ClearingSystem).HasColumnName($"{prefix}_clearing_system").HasMaxLength(5);
        a.Property(x => x.MemberId).HasColumnName($"{prefix}_member_id").HasMaxLength(35);
        a.Property(x => x.Name).HasColumnName($"{prefix}_name").HasMaxLength(140);
    }
}

/// <summary>Refuses any update or delete: the store is insert-only (plan W2, the review's item 20).</summary>
public sealed class InsertOnlyGuard : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    static void Check(DbContext? ctx)
    {
        if (ctx is null) return;
        foreach (var e in ctx.ChangeTracker.Entries())
        {
            if (e.State is EntityState.Modified or EntityState.Deleted)
                throw new InsertOnlyViolation($"{e.Metadata.ClrType.Name} is insert-only: it may not be {(e.State == EntityState.Deleted ? "deleted" : "updated")}. Record a new fact instead.");
        }
    }
}

public sealed class InsertOnlyViolation(string message) : InvalidOperationException(message)
{
    /// <summary>The error dictionary's code (Issue I-103).</summary>
    public string Code => "store.InsertOnlyViolation";
}
