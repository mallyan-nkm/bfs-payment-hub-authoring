using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;

namespace TransactHub.Engines;

/// <summary>
/// What every engine binding tells an operator (R-09-23 to R-09-28, I-66): one span per activity attempt and
/// per run, and the payments, activity and queue metrics, under one set of attribute names. It uses only
/// System.Diagnostics, so it costs nothing when no listener is attached (the studio's own harnesses); a
/// runtime host attaches OpenTelemetry (TransactHub.Telemetry). Attributes carry ids, names of documents
/// and outcomes, never a value from the payment's payload.
/// </summary>
public static class EngineTelemetry
{
    public const string SourceName = "TransactHub.Engine";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
    public static readonly Meter Meter = new(SourceName, "1.0.0");

    /// <summary>The engine this process binds: temporal, dtfx, elsa or flowable. Set by the host before it runs.</summary>
    public static string Engine = "unknown";

    /// <summary>Where a log line goes: level (0 debug, 1 information, 2 warning, 3 error), a template with named holes, and its values.</summary>
    public static Action<int, string, object?[]>? LogSink;

    public static readonly Counter<long> Payments = Meter.CreateCounter<long>("th.payments", "{payment}", "Payments (a file is one) that ended, by outcome, scheme, tenant and engine");
    public static readonly Counter<long> PaymentLines = Meter.CreateCounter<long>("th.payment.lines", "{line}", "Lines (items) of the payments that ended");
    public static readonly Histogram<double> PaymentDuration = Meter.CreateHistogram<double>("th.payment.duration", "s", "Wall time from the start of a payment's run to its end");
    public static readonly Histogram<double> AttemptDuration = Meter.CreateHistogram<double>("th.activity.duration", "s", "Wall time of one activity attempt, as the engine binding sees it");
    public static readonly Counter<long> AttemptErrors = Meter.CreateCounter<long>("th.activity.errors", "{attempt}", "Activity attempts that ended in an error");
    static int inFlight;
    static readonly Dictionary<string, int> inFlightByQueue = new();
    static readonly ObservableGauge<int> InFlight = Meter.CreateObservableGauge("th.queue.in_flight", ObserveInFlight, "{attempt}", "Activity attempts sent to a worker queue and not yet answered");

    static IEnumerable<Measurement<int>> ObserveInFlight()
    {
        lock (inFlightByQueue)
            return inFlightByQueue.Select(kv => new Measurement<int>(kv.Value, new KeyValuePair<string, object?>("th.queue", kv.Key))).ToList();
    }

    /// <summary>The trace a run belongs to when the caller started it (TRACEPARENT, as the deploy runner sets it), else none.</summary>
    public static readonly ActivityContext ProcessParent = ParseParent(Environment.GetEnvironmentVariable("TRACEPARENT"), Environment.GetEnvironmentVariable("TRACESTATE"));

    static ActivityContext ParseParent(string? traceparent, string? tracestate) =>
        traceparent is { Length: > 0 } && ActivityContext.TryParse(traceparent, tracestate, isRemote: true, out var c) ? c : default;

    /// <summary>The parent of a new span: the current span, else the run's trace from the caller.</summary>
    static ActivityContext Parent => Activity.Current?.Context ?? ProcessParent;

    /// <summary>The span of a whole run on this engine (one deployment, one file).</summary>
    public static Activity? StartRun(string engine, JsonObject? plan = null)
    {
        Engine = engine;
        var a = Source.StartActivity("th.engine.run", ActivityKind.Internal, Parent);
        if (a is null) return null;
        a.SetTag("th.engine", engine);
        if (plan is not null)
        {
            var (scheme, tenant, paymentId) = Who(plan);
            a.SetTag("th.scheme", scheme);
            a.SetTag("th.tenant", tenant);
            a.SetTag("th.payment.id", paymentId);
            a.SetTag("th.workflow", J.S(J.P(plan, "built"), "workflow"));
        }
        Log(1, "Run started on {th.engine}", engine);
        return a;
    }

    /// <summary>The scheme, tenant and payment id a plan names (references only: the header, never the payload).</summary>
    public static (string? scheme, string? tenant, string? paymentId) Who(JsonObject plan)
    {
        var built = J.P(plan, "built");
        var header = J.P(J.P(built, "context"), "header");
        return (J.S(header, "scheme"), J.S(plan, "tenant") ?? J.S(J.P(built, "config"), "tenant"), J.S(header, "paymentId"));
    }

    /// <summary>One attempt sent to a worker or the party service. Dispose the scope when the answer is in.</summary>
    public static AttemptScope Attempt(JsonObject req, string? queue)
    {
        var activity = J.S(req, "activity") ?? "?";
        var a = Source.StartActivity("th.attempt " + activity, ActivityKind.Client, Parent);
        if (a is not null)
        {
            a.SetTag("th.engine", Engine);
            a.SetTag("th.activity", activity);
            a.SetTag("th.release", J.S(req, "release"));
            a.SetTag("th.step", J.S(req, "step"));
            a.SetTag("th.attempt", J.Num(req, "attempt"));
            a.SetTag("th.instance", J.S(req, "instance"));
            a.SetTag("th.queue", queue);
            if (J.S(req, "level") is string l) a.SetTag("th.level", l);
            if (J.S(J.P(J.P(req, "ctx"), "header"), "paymentId") is string pid) a.SetTag("th.payment.id", pid);
        }
        return new AttemptScope(a, activity, queue ?? "party");
    }

    public sealed class AttemptScope : IDisposable
    {
        readonly Activity? span;
        readonly string activity, queue;
        readonly long started = Stopwatch.GetTimestamp();
        string kind = "error";
        string? outcome, error;

        internal AttemptScope(Activity? span, string activity, string queue)
        {
            this.span = span;
            this.activity = activity;
            this.queue = queue;
            Interlocked.Increment(ref inFlight);
            lock (inFlightByQueue) inFlightByQueue[queue] = inFlightByQueue.GetValueOrDefault(queue) + 1;
        }

        /// <summary>What the worker answered: its kind (result, error, hang), outcome and error name.</summary>
        public void Answered(JsonObject res)
        {
            kind = J.S(res, "kind") ?? "result";
            outcome = J.S(res, "outcome");
            error = kind == "error" ? J.S(res, "name") ?? "Error" : null;
        }

        public void Failed(Exception e)
        {
            kind = "error";
            error = e.GetType().Name;
            // The type only: an exception's message may quote what it was given.
            span?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection { ["exception.type"] = e.GetType().FullName }));
        }

        public void Dispose()
        {
            Interlocked.Decrement(ref inFlight);
            lock (inFlightByQueue) inFlightByQueue[queue] = inFlightByQueue.GetValueOrDefault(queue) - 1;
            var tags = new TagList { { "th.engine", Engine }, { "th.activity", activity }, { "th.kind", kind } };
            if (outcome is not null) tags.Add("th.outcome", outcome);
            AttemptDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            if (error is not null)
            {
                AttemptErrors.Add(1, new TagList { { "th.engine", Engine }, { "th.activity", activity }, { "th.error", error } });
                Log(2, "Attempt of {th.activity} on {th.engine} ended in {th.error}", activity, Engine, error);
            }
            if (span is not null)
            {
                span.SetTag("th.kind", kind);
                if (outcome is not null) span.SetTag("th.outcome", outcome);
                if (error is not null) { span.SetTag("th.error", error); span.SetStatus(ActivityStatusCode.Error, error); }
                span.Dispose();
            }
        }
    }

    /// <summary>What a harness observed: one payment per root instance, by the outcome its trace ended with.</summary>
    public static void Observed(JsonObject plan, JsonObject obs, TimeSpan elapsed)
    {
        var (scheme, tenant, _) = Who(plan);
        var lines = J.P(J.P(J.P(plan, "built"), "context"), "items") is JsonArray items ? items.Count : 0;
        foreach (var inst in J.A(obs, "instances"))
        {
            var trace = J.A(inst, "trace");
            var last = trace.Count > 0 ? trace[^1]?.GetValue<string>() ?? "" : "";
            var outcome = J.S(inst, "failed") is not null ? "failed" : last.StartsWith("end:") ? last[4..] : "open";
            var tags = new TagList { { "th.outcome", outcome }, { "th.scheme", scheme }, { "th.tenant", tenant }, { "th.engine", Engine } };
            Payments.Add(1, tags);
            if (lines > 0) PaymentLines.Add(lines, tags);
            PaymentDuration.Record(elapsed.TotalSeconds, tags);
            Activity.Current?.SetTag("th.outcome", outcome);
            Log(1, "Payment ended {th.outcome} on {th.engine} for {th.scheme}", outcome, Engine, scheme);
        }
    }

    public static void Log(int level, string template, params object?[] values) => LogSink?.Invoke(level, template, values);
}
