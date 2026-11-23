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
    /// The awaited replies the gauge watches: none unless the installation names them. Platform code names no scheme (R-09-142); the
    /// scheme declares no reply to a message it sends yet (Issue I-235), so an installation names each scheme's in its settings.
    /// </summary>
    public static readonly AwaitedReply[] Default = [];

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
        foreach (var r in replies)
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
