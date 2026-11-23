using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

namespace TransactHub.Ports;

// Circuit breakers in the production adapters that call outside systems (R-09-155, owner's decision of 23 November 2026,
// Issue I-189 (5), docs/OWNER-ACTIONS.md A2): core banking and the bank's other gateways, SFTP, MQTT, Kafka and webhooks. After a
// run of failures an adapter stops calling the system for a while (open), then lets one call through (half-open): a success closes
// the breaker, a failure opens it again. A call refused by an open breaker fails as a transport failure does, so the step's own
// retry policy takes it, and nothing reaches the wire. The thresholds are dials of the binding, with defaults and bounds.

/// <summary>Where a breaker is: calling (closed), not calling (open), or letting one trial call through (half-open).</summary>
public enum BreakerState { Closed, Open, HalfOpen }

/// <summary>
/// A breaker's dials, read from its binding's settings: <c>breakerFailures</c>, the failures in a row that open it (default 5,
/// from 1 to 100), and <c>breakerOpenSeconds</c>, how long it stays open before one trial call (default 30, from 1 to 3600). A
/// value outside its bounds is refused, never clamped.
/// </summary>
public sealed record BreakerDials(int Failures, TimeSpan OpenFor)
{
    public const string FailuresSetting = "breakerFailures", OpenSecondsSetting = "breakerOpenSeconds";
    public const int DefaultFailures = 5, MinFailures = 1, MaxFailures = 100;
    public const int DefaultOpenSeconds = 30, MinOpenSeconds = 1, MaxOpenSeconds = 3600;
    public static readonly BreakerDials Default = new(DefaultFailures, TimeSpan.FromSeconds(DefaultOpenSeconds));

    public static BreakerDials Of(PortBinding b)
    {
        static int Read(PortBinding b, string key, int fallback, int min, int max)
        {
            var s = b.Setting(key, fallback.ToString(CultureInfo.InvariantCulture));
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
                throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: {key} is '{s}', not a whole number from {min} to {max}");
            return v;
        }
        return new(Read(b, FailuresSetting, DefaultFailures, MinFailures, MaxFailures), TimeSpan.FromSeconds(Read(b, OpenSecondsSetting, DefaultOpenSeconds, MinOpenSeconds, MaxOpenSeconds)));
    }
}

/// <summary>
/// A call an open breaker refused: nothing was sent. It is a transport failure (an <see cref="HttpRequestException"/>), so a caller
/// retries it as it retries an outside system that does not answer.
/// </summary>
public sealed class CircuitOpenException(BreakerKey key, DateTimeOffset until)
    : HttpRequestException($"{key.Port} ({key.Adapter}) to {key.Target} for {key.Tenant}: the circuit breaker is open until {until:O}; nothing was sent", null, HttpStatusCode.ServiceUnavailable)
{
    public BreakerKey Key => key;
    public DateTimeOffset Until => until;
}

/// <summary>What a breaker guards: one outside system (host and port, broker or partner) of one adapter of one tenant's port.</summary>
public sealed record BreakerKey(string Tenant, string Port, string Adapter, string Target)
{
    public override string ToString() => $"{Tenant}|{Port}|{Adapter}|{Target}";
}

/// <summary>A breaker that moved: its new state, the failures in a row that moved it, and when. Ids and hosts only, never a payment.</summary>
public sealed record BreakerChange(BreakerKey Key, BreakerState State, int Failures, DateTimeOffset At, string? Why);

/// <summary>Where a host records its breakers' moves, so the service console's health shows an open breaker (R-09-156).</summary>
public interface IBreakerJournal
{
    void Record(BreakerChange change);
}

/// <summary>One breaker. Thread-safe; a breaker in the half-open state lets exactly one call through at a time.</summary>
public sealed class CircuitBreaker
{
    readonly object gate = new();
    readonly Func<DateTimeOffset> now;
    readonly Action<BreakerChange>? moved;
    int failures;
    DateTimeOffset openedAt;
    bool trialInFlight;

    public CircuitBreaker(BreakerKey key, BreakerDials dials, Func<DateTimeOffset>? now = null, Action<BreakerChange>? moved = null)
    {
        Key = key;
        Dials = dials;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.moved = moved;
    }

    public BreakerKey Key { get; }
    /// <summary>The dials in use; a binding with new dials changes them for the next call.</summary>
    public BreakerDials Dials { get; set; }
    public BreakerState State { get; private set; } = BreakerState.Closed;
    public int Failures { get { lock (gate) return failures; } }
    /// <summary>Until when an open breaker refuses calls.</summary>
    public DateTimeOffset? OpenUntil { get { lock (gate) return State == BreakerState.Closed ? null : openedAt + Dials.OpenFor; } }

    /// <summary>
    /// Runs one call through the breaker. <paramref name="failed"/> says whether an answer is a failure of the outside system (for
    /// HTTP, a 5xx or a 429); <paramref name="counts"/> whether an exception is one (default: every exception but a refused binding
    /// and the caller's own cancellation). A business refusal is an answer, not a failure.
    /// </summary>
    public async Task<T> Run<T>(Func<Task<T>> call, Func<T, bool>? failed = null, Func<Exception, bool>? counts = null, CancellationToken ct = default)
    {
        Enter();
        T r;
        try { r = await call(); }
        catch (Exception e) when ((counts ?? (x => Counts(x, ct)))(e))
        {
            Failed(e.GetType().Name);
            throw;
        }
        catch
        {
            Released();
            throw;
        }
        if (failed?.Invoke(r) == true) Failed("answer"); else Succeeded();
        return r;
    }

    public Task Run(Func<Task> call, Func<Exception, bool>? counts = null, CancellationToken ct = default) =>
        Run<int>(async () => { await call(); return 0; }, null, counts, ct);

    public static bool Counts(Exception e, CancellationToken ct) =>
        e is not PortBindingException && e is not CircuitOpenException && !(e is OperationCanceledException && ct.IsCancellationRequested);

    void Enter()
    {
        BreakerChange? change = null;
        lock (gate)
        {
            if (State == BreakerState.Open)
            {
                var until = openedAt + Dials.OpenFor;
                if (now() < until) throw new CircuitOpenException(Key, until);
                State = BreakerState.HalfOpen;
                change = new(Key, State, failures, now(), "open period over: one trial call");
            }
            if (State == BreakerState.HalfOpen)
            {
                if (trialInFlight) throw new CircuitOpenException(Key, now() + TimeSpan.FromSeconds(1));
                trialInFlight = true;
            }
        }
        if (change is not null) moved?.Invoke(change);
    }

    void Released()
    {
        lock (gate) trialInFlight = false;
    }

    void Succeeded()
    {
        BreakerChange? change = null;
        lock (gate)
        {
            trialInFlight = false;
            if (State != BreakerState.Closed) change = new(Key, BreakerState.Closed, failures, now(), "a call succeeded");
            State = BreakerState.Closed;
            failures = 0;
        }
        if (change is not null) moved?.Invoke(change);
    }

    void Failed(string why)
    {
        BreakerChange? change = null;
        lock (gate)
        {
            trialInFlight = false;
            failures++;
            if (State == BreakerState.HalfOpen || (State == BreakerState.Closed && failures >= Dials.Failures))
            {
                State = BreakerState.Open;
                openedAt = now();
                change = new(Key, State, failures, openedAt, why);
            }
        }
        if (change is not null) moved?.Invoke(change);
    }
}

/// <summary>
/// The breakers of a process, one per outside system of each binding (<see cref="BreakerKey"/>). <see cref="Shared"/> is the one the
/// production adapters use; a host sets its <see cref="Journal"/> so the service console sees its breakers (R-09-156).
/// </summary>
public sealed class CircuitBreakers
{
    public static CircuitBreakers Shared { get; } = new();

    /// <summary>The error dictionary's code of a call an open breaker refused, and of the health probe that finds one open (Issue I-259).</summary>
    public const string OpenCode = "port.CircuitOpen";

    readonly ConcurrentDictionary<BreakerKey, CircuitBreaker> all = new();
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
    public IBreakerJournal? Journal { get; set; }

    /// <summary>The breaker for a binding's target, with the binding's dials (refused when out of bounds).</summary>
    public CircuitBreaker For(PortBinding b, string target)
    {
        var dials = BreakerDials.Of(b);
        var key = new BreakerKey(b.Tenant, b.Port, b.Adapter, target);
        var br = all.GetOrAdd(key, k => new CircuitBreaker(k, dials, () => Now(), Moved));
        br.Dials = dials;
        return br;
    }

    public IReadOnlyList<CircuitBreaker> All => all.Values.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal).ToList();

    void Moved(BreakerChange c)
    {
        try { Journal?.Record(c); } catch { /* the breaker works without its journal; the next move is recorded again */ }
    }

    /// <summary>The target of a URL: its host and port.</summary>
    public static string TargetOf(Uri u) => $"{u.Host}:{u.Port}";
}

/// <summary>HTTP calls through a breaker: a 5xx or a 429 is a failure of the outside system; any other answer is the caller's to read.</summary>
public static class GuardedHttp
{
    /// <param name="ct">The send's token, a timeout's among them.</param>
    /// <param name="caller">The caller's own token: a call it cancelled is not a failure; a timeout is.</param>
    public static Task<HttpResponseMessage> Send(HttpClient http, PortBinding b, HttpRequestMessage m, CancellationToken ct, CancellationToken caller = default, CircuitBreakers? breakers = null) =>
        (breakers ?? CircuitBreakers.Shared).For(b, CircuitBreakers.TargetOf(m.RequestUri!))
            .Run(() => http.SendAsync(m, ct), r => (int)r.StatusCode >= 500 || r.StatusCode == HttpStatusCode.TooManyRequests, ct: caller);
}
