using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>A reply a sent message waits for: its scheme, the kind sent and the kinds that answer it (an ACK or a NAK file).</summary>
public sealed record AwaitedReply(string Scheme, string Sent, IReadOnlyList<string> Replies);

/// <summary>
/// What the alert rules read that no engine host knows alone (R-09-72): the open payments per distribution, the oldest message
/// still waiting for its scheme's reply, and the oldest hold still waiting for its decision, read from the store and the
/// operations read model each time <see cref="Refresh"/> runs (the service console's health sampler), and exported as
/// gauges. Holds that pass their expiry with no decision are counted where they are expired (<see cref="HoldsExpired"/>).
/// The gauges carry ids only: a distribution, a scheme, a message kind, a tenant.
/// </summary>
public sealed class RuntimeGauges
{
    public const string MeterName = "TransactHub.Operations";
    public static readonly Meter Meter = new(MeterName, "1.0.0");
    public static readonly Counter<long> HoldsExpired = Meter.CreateCounter<long>("th.holds.expired", "{hold}", "Holds that reached their expiry with no decision");

    /// <summary>
    /// The awaited replies the installation adds: none by default. Platform code names no scheme (R-09-142); the gauge reads the pairs
    /// from the schemes of the active distributions (a message's <c>repliesTo</c>, R-09-211, Issue I-235), and the setting only adds to them.
    /// </summary>
    public static readonly AwaitedReply[] Default = [];

    /// <summary>The awaited replies a scheme manifest declares: for each message sent that another answers, the messages that answer it.</summary>
    public static IEnumerable<AwaitedReply> Declared(string scheme, System.Text.Json.Nodes.JsonObject manifest) =>
        (manifest["messages"] as System.Text.Json.Nodes.JsonArray ?? [])
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .SelectMany(m => (m["repliesTo"] as System.Text.Json.Nodes.JsonArray ?? []).Select(s => (sent: (string?)s, reply: (string?)m["id"])))
            .Where(x => x.sent is { Length: > 0 } && x.reply is { Length: > 0 })
            .GroupBy(x => x.sent!, StringComparer.Ordinal)
            .Select(g => new AwaitedReply(scheme, g.Key, g.Select(x => x.reply!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()));

    /// <summary>The pairs read from one distribution's package, kept by its id: a stored distribution never changes.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<AwaitedReply>> declaredOf = new(StringComparer.Ordinal);

    /// <summary>
    /// The awaited replies the schemes of every distribution active in some environment declare, then the installation's, each pair
    /// once (R-09-211).
    /// </summary>
    public static async Task<IReadOnlyList<AwaitedReply>> InForce(StoreDbContext db, IReadOnlyList<AwaitedReply> configured, CancellationToken ct = default)
    {
        var reg = new DistributionRegistry(db);
        var all = new List<AwaitedReply>();
        foreach (var bank in await reg.Banks(ct))
            foreach (var dist in await reg.Of(bank, ct))
            {
                if (!(await reg.StatesOf(dist, ct)).Values.Any(s => s.State == "active")) continue;
                if (!declaredOf.TryGetValue(dist, out var pairs))
                {
                    var (b, id) = DistributionRegistry.Split(dist);
                    var row = await new ReleaseRegistry(db).Get(DistributionRegistry.Scope(b), id, ct);
                    pairs = row is null ? [] : UploadCatalogue.Schemes(row.Package).SelectMany(kv => Declared(kv.Key, kv.Value)).ToList();
                    declaredOf[dist] = pairs;
                }
                all.AddRange(pairs);
            }
        all.AddRange(configured);
        return all.GroupBy(r => (r.Scheme, r.Sent))
            .Select(g => new AwaitedReply(g.Key.Scheme, g.Key.Sent, g.SelectMany(r => r.Replies).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()))
            .OrderBy(r => r.Scheme, StringComparer.Ordinal).ThenBy(r => r.Sent, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The awaited replies an installation names (<c>Service:Gauges:AwaitedReplies</c>): a JSON array of <c>{ scheme, sent, replies }</c>,
    /// for example a WPS salary file and its acknowledgement.
    /// </summary>
    public static IReadOnlyList<AwaitedReply> FromSetting(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        return (System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray ?? throw new InvalidOperationException("Service:Gauges:AwaitedReplies is a JSON array of { scheme, sent, replies }"))
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .Select(o => new AwaitedReply((string?)o["scheme"] ?? throw new InvalidOperationException("an awaited reply names its scheme"), (string?)o["sent"] ?? throw new InvalidOperationException("an awaited reply names the message sent"),
                (o["replies"] as System.Text.Json.Nodes.JsonArray ?? []).Select(r => (string)r!).ToList()))
            .ToList();
    }

    readonly StoreOptions store;
    readonly StoreOptions ops;
    readonly IReadOnlyList<AwaitedReply> replies;
    readonly TimeProvider clock;
    volatile Snapshot last = new([], [], []);
    /// <summary>The instance a process exports: the latest one made (a host makes one).</summary>
    static volatile RuntimeGauges? active;

    sealed record Snapshot(List<Measurement<long>> Open, List<Measurement<double>> AwaitAge, List<Measurement<double>> HoldAge);

    static readonly ObservableGauge<long> OpenPayments = Meter.CreateObservableGauge("th.payments.open", () => active?.last.Open ?? [], "{payment}", "Payments pinned to a distribution that have not ended, by distribution");
    static readonly ObservableGauge<double> AwaitOldest = Meter.CreateObservableGauge("th.await.oldest", () => active?.last.AwaitAge ?? [], "s", "Age of the oldest message sent that waits for its scheme's reply, by scheme, message and tenant");
    static readonly ObservableGauge<double> HoldsOldest = Meter.CreateObservableGauge("th.holds.oldest", () => active?.last.HoldAge ?? [], "s", "Age of the oldest hold that waits for its decision, by tenant and hold kind");

    public RuntimeGauges(StoreOptions store, StoreOptions ops, IReadOnlyList<AwaitedReply>? replies = null, TimeProvider? clock = null)
    {
        this.store = store;
        this.ops = ops;
        this.replies = replies ?? Default;
        this.clock = clock ?? TimeProvider.System;
        active = this;
    }

    /// <summary>The awaited replies the last refresh watched: the active distributions' schemes', then the installation's.</summary>
    public IReadOnlyList<AwaitedReply> Watched { get; private set; } = [];

    /// <summary>The values as the last refresh read them, for tests.</summary>
    public (IReadOnlyList<Measurement<long>> open, IReadOnlyList<Measurement<double>> awaitAge, IReadOnlyList<Measurement<double>> holdAge) Current => (last.Open, last.AwaitAge, last.HoldAge);

    public async Task Refresh(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await using var db = StoreDbContext.Open(store);
        var open = (await new DistributionRegistry(db, clock).OpenPayments(ct))
            .Select(kv => new Measurement<long>(kv.Value, new KeyValuePair<string, object?>("th.distribution", kv.Key))).ToList();
        await using var o = OpsDbContext.Open(ops);
        var awaitAge = new List<Measurement<double>>();
        Watched = await InForce(db, replies, ct);
        foreach (var r in Watched)
        {
            var kinds = r.Replies.Append(r.Sent).ToArray();
            var docs = await o.Documents.AsNoTracking().Where(d => d.Scheme == r.Scheme && d.SubjectId != null && kinds.Contains(d.Kind))
                .Select(d => new { d.SubjectId, d.Tenant, d.Kind, d.Direction, d.At }).ToListAsync(ct);
            foreach (var t in docs.GroupBy(d => d.Tenant))
            {
                var waiting = t.GroupBy(d => d.SubjectId).Select(g => (sent: g.Where(d => d.Kind == r.Sent && d.Direction.StartsWith("out")).Select(d => (DateTimeOffset?)d.At).Max(),
                        reply: g.Where(d => r.Replies.Contains(d.Kind) && d.Direction.StartsWith("in")).Select(d => (DateTimeOffset?)d.At).Max()))
                    .Where(x => x.sent is not null && (x.reply is null || x.reply < x.sent)).Select(x => (now - x.sent!.Value).TotalSeconds).ToList();
                awaitAge.Add(new Measurement<double>(waiting.Count == 0 ? 0 : Math.Max(0, waiting.Max()),
                    new("th.scheme", r.Scheme), new("th.message", r.Sent), new("th.tenant", t.Key)));
            }
        }
        var holds = await o.Holds.AsNoTracking().Where(h => h.Status == "open").Select(h => new { h.Tenant, h.Kind, h.OpenedAt }).ToListAsync(ct);
        var holdAge = holds.GroupBy(h => (h.Tenant, h.Kind))
            .Select(g => new Measurement<double>(Math.Max(0, (now - g.Min(h => h.OpenedAt)).TotalSeconds), new("th.tenant", g.Key.Tenant), new("th.hold.kind", g.Key.Kind))).ToList();
        last = new Snapshot(open, awaitAge, holdAge);
    }
}
