using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TransactHub.Store;

/// <summary>
/// Every write to the store, seen once, whoever writes it (R-09-33, I-66): a span and the write latency, by
/// what was written (the row kinds) and on which database. It uses only System.Diagnostics; a host attaches
/// OpenTelemetry. The span names row kinds and counts, never a column value.
/// </summary>
public static class StoreTelemetry
{
    public const string SourceName = "TransactHub.Store";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
    public static readonly Meter Meter = new(SourceName, "1.0.0");
    public static readonly Histogram<double> WriteDuration = Meter.CreateHistogram<double>("th.store.write.duration", "s", "Wall time of one save to the store, by row kinds and database");
    public static readonly Counter<long> WriteErrors = Meter.CreateCounter<long>("th.store.write.errors", "{save}", "Saves to the store that failed (a lost race for a fact number among them)");
    public static readonly Counter<long> Rows = Meter.CreateCounter<long>("th.store.rows", "{row}", "Rows inserted into the store, by kind");
}

/// <summary>Times each SaveChanges, per context.</summary>
sealed class StoreWriteTelemetry(StoreProvider provider) : SaveChangesInterceptor
{
    readonly string providerName = provider.ToString();
    readonly string system = provider == StoreProvider.SqlServer ? "microsoft.sql_server" : "postgresql";

    sealed class Save
    {
        public Activity? Span;
        public long Started;
        public string Op = "";
    }

    readonly ConditionalWeakTable<DbContext, Save> saves = new();

    void Begin(DbContext? db)
    {
        if (db is null) return;
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in db.ChangeTracker.Entries())
            if (e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                var k = e.Metadata.ClrType.Name;
                k = (k.EndsWith("Row", StringComparison.Ordinal) ? k[..^3] : k).ToLowerInvariant();
                kinds[k] = kinds.GetValueOrDefault(k) + 1;
            }
        var s = saves.GetOrCreateValue(db);
        s.Op = kinds.Count == 0 ? "none" : string.Join('+', kinds.Keys);
        foreach (var (k, n) in kinds) StoreTelemetry.Rows.Add(n, new TagList { { "th.store.kind", k }, { "th.store.provider", providerName } });
        s.Started = Stopwatch.GetTimestamp();
        s.Span = StoreTelemetry.Source.StartActivity("th.store.write " + s.Op, ActivityKind.Client);
        if (s.Span is null) return;
        s.Span.SetTag("db.system.name", system);
        s.Span.SetTag("db.operation.name", "insert");
        s.Span.SetTag("th.store.op", s.Op);
        s.Span.SetTag("th.store.provider", providerName);
        s.Span.SetTag("th.store.rows", kinds.Values.Sum());
    }

    void End(DbContext? db, Exception? error)
    {
        if (db is null || !saves.TryGetValue(db, out var s)) return;
        var tags = new TagList { { "th.store.op", s.Op }, { "th.store.provider", providerName }, { "th.outcome", error is null ? "ok" : "error" } };
        StoreTelemetry.WriteDuration.Record(Stopwatch.GetElapsedTime(s.Started).TotalSeconds, tags);
        if (error is not null)
        {
            StoreTelemetry.WriteErrors.Add(1, new TagList { { "th.store.op", s.Op }, { "th.store.provider", providerName }, { "th.error", error.GetType().Name } });
            // The type only: a database error's message can quote the row.
            s.Span?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
        }
        s.Span?.Dispose();
        s.Span = null;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Begin(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Begin(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        End(eventData.Context, null);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        End(eventData.Context, null);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => End(eventData.Context, eventData.Exception);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        End(eventData.Context, eventData.Exception);
        return Task.CompletedTask;
    }
}
