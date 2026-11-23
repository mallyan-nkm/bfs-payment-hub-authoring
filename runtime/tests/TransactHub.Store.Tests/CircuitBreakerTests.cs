using System.Net;
using TransactHub.Ports;
using Xunit;

namespace TransactHub.Store.Tests;

/// <summary>
/// Circuit breakers in the production adapters (R-09-155, owner's decision of 23 November 2026, Issue I-189): after a run of
/// failures the adapter stops calling the outside system for a while (open), then lets one call through (half-open); a success
/// closes it, a failure opens it again. The thresholds are dials of the binding with defaults and bounds. Synthetic hosts only.
/// </summary>
public sealed class CircuitBreakerTests
{
    sealed class Clock(DateTimeOffset t)
    {
        public DateTimeOffset Now = t;
    }

    sealed class Journal : IBreakerJournal
    {
        public readonly List<BreakerChange> Moves = new();
        public void Record(BreakerChange c) { lock (Moves) Moves.Add(c); }
    }

    /// <summary>An outside system that answers each call with the next status of its list, the last one from then on.</summary>
    sealed class Gateway(params HttpStatusCode[] answers) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var i = Interlocked.Increment(ref Calls) - 1;
            var code = answers[Math.Min(i, answers.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(code == HttpStatusCode.OK ? "{\"result\":\"clear\",\"reference\":\"r-1\"}" : "{}") });
        }
    }

    static PortBinding Bind(string port, string adapter, Dictionary<string, string> settings, string tenant = "bank-breaker") =>
        new(tenant, port, adapter, DateTimeOffset.Parse("2026-11-01T00:00:00Z"), settings);

    static readonly BreakerKey Key = new("bank-breaker", "p.port.core-banking.v1", "gateway-rest", "core.bank-breaker.invalid:443");

    [Fact]
    public async Task A_breaker_opens_after_its_failures_in_a_row_refuses_while_open_lets_one_trial_through_and_closes_on_a_success()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-24T08:00:00Z"));
        var journal = new Journal();
        var br = new CircuitBreaker(Key, new BreakerDials(3, TimeSpan.FromSeconds(30)), () => clock.Now, journal.Record);
        var calls = 0;
        Task<int> Fails() { calls++; throw new HttpRequestException("the gateway does not answer"); }
        Task<int> Works() { calls++; return Task.FromResult(1); }

        // Closed: failures below the threshold, and a success clears the run.
        await Assert.ThrowsAsync<HttpRequestException>(() => br.Run(Fails));
        await Assert.ThrowsAsync<HttpRequestException>(() => br.Run(Fails));
        Assert.Equal(1, await br.Run(Works));
        Assert.Equal(BreakerState.Closed, br.State);
        Assert.Equal(0, br.Failures);

        // Three in a row open it.
        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<HttpRequestException>(() => br.Run(Fails));
        Assert.Equal(BreakerState.Open, br.State);
        Assert.Equal(6, calls);

        // Open: nothing is called, and the refusal is a transport failure the caller retries.
        var refused = await Assert.ThrowsAsync<CircuitOpenException>(() => br.Run(Works));
        Assert.IsAssignableFrom<HttpRequestException>(refused);
        Assert.Equal(clock.Now.AddSeconds(30), refused.Until);
        Assert.Equal(6, calls);

        // After the open period, one trial call: it fails, and the breaker opens again for another period.
        clock.Now = clock.Now.AddSeconds(31);
        await Assert.ThrowsAsync<HttpRequestException>(() => br.Run(Fails));
        Assert.Equal(BreakerState.Open, br.State);
        await Assert.ThrowsAsync<CircuitOpenException>(() => br.Run(Works));
        Assert.Equal(7, calls);

        // Half-open lets one call through at a time: a second call while the trial is out is refused.
        clock.Now = clock.Now.AddSeconds(31);
        var release = new TaskCompletionSource<int>();
        var trial = br.Run(() => { calls++; return release.Task; });
        Assert.Equal(BreakerState.HalfOpen, br.State);
        await Assert.ThrowsAsync<CircuitOpenException>(() => br.Run(Works));
        release.SetResult(1);
        Assert.Equal(1, await trial);
        Assert.Equal(BreakerState.Closed, br.State);
        Assert.Equal(1, await br.Run(Works));

        // Each move is recorded, in order: open, half-open, open, half-open, closed.
        Assert.Equal([BreakerState.Open, BreakerState.HalfOpen, BreakerState.Open, BreakerState.HalfOpen, BreakerState.Closed], journal.Moves.Select(m => m.State));
        Assert.All(journal.Moves, m => Assert.Equal(Key, m.Key));
    }

    [Fact]
    public async Task A_refused_binding_and_the_callers_own_cancellation_are_not_failures_of_the_outside_system()
    {
        var br = new CircuitBreaker(Key, new BreakerDials(1, TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAsync<PortBindingException>(() => br.Run<int>(() => throw new PortBindingException("no baseUrl")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => br.Run<int>(() => throw new OperationCanceledException(cts.Token), ct: cts.Token));
        Assert.Equal(BreakerState.Closed, br.State);
        // A timeout, which the caller did not ask for, is one.
        await Assert.ThrowsAsync<TaskCanceledException>(() => br.Run<int>(() => throw new TaskCanceledException("timed out")));
        Assert.Equal(BreakerState.Open, br.State);
    }

    [Fact]
    public void The_dials_have_defaults_and_bounds_and_a_value_outside_them_is_refused()
    {
        var b = Bind("p.port.core-banking.v1", "gateway-rest", new() { ["baseUrl"] = "https://core.bank-breaker.invalid/" });
        Assert.Equal(BreakerDials.Default, BreakerDials.Of(b));
        Assert.Equal(new BreakerDials(5, TimeSpan.FromSeconds(30)), BreakerDials.Default);
        Assert.Equal(new BreakerDials(1, TimeSpan.FromSeconds(3600)), BreakerDials.Of(b with { Settings = new Dictionary<string, string> { ["breakerFailures"] = "1", ["breakerOpenSeconds"] = "3600" } }));
        foreach (var (k, v) in new[] { ("breakerFailures", "0"), ("breakerFailures", "101"), ("breakerFailures", "many"), ("breakerOpenSeconds", "0"), ("breakerOpenSeconds", "3601") })
            Assert.Throws<PortBindingException>(() => BreakerDials.Of(b with { Settings = new Dictionary<string, string> { [k] = v } }));
    }

    [Fact]
    public async Task Core_banking_behind_a_failing_gateway_stops_calling_it_and_a_business_refusal_is_no_failure()
    {
        var breakers = CircuitBreakers.Shared;
        var tenant = $"bank-cb-{Guid.NewGuid():N}"[..16];
        var gw = new Gateway(HttpStatusCode.ServiceUnavailable);
        var b = Bind("p.port.core-banking.v1", "gateway-rest", new()
        {
            ["baseUrl"] = "https://core.bank-breaker.invalid/", ["serviceCode"] = "SVC", ["balancePath"] = "/balance", ["balanceFunctionId"] = "F1",
            ["externalHostId"] = "H1", ["txnFlag"] = "N", ["breakerFailures"] = "2", ["breakerOpenSeconds"] = "60",
        }, tenant);
        var core = CoreBankingAdapters.Create(b, new HttpClient(gw));
        await Assert.ThrowsAsync<HttpRequestException>(() => core.Balance("001", "0010000000001"));
        await Assert.ThrowsAsync<HttpRequestException>(() => core.Balance("001", "0010000000001"));
        var open = await Assert.ThrowsAsync<CircuitOpenException>(() => core.Balance("001", "0010000000001"));
        Assert.Equal(2, gw.Calls);
        Assert.Equal(tenant, open.Key.Tenant);
        Assert.Equal("core.bank-breaker.invalid:443", open.Key.Target);
        Assert.Contains(breakers.All, x => x.Key == open.Key && x.State == BreakerState.Open);

        // A 4xx is the gateway's answer, not its failure: the screening engine's refusal leaves its breaker closed.
        var screening = new Gateway(HttpStatusCode.BadRequest);
        var s = ScreeningAdapters.Create(Bind(ScreeningAdapters.Sanctions, "gateway-rest", new() { ["baseUrl"] = "https://screening.bank-breaker.invalid/", ["breakerFailures"] = "1" }, tenant), new HttpClient(screening));
        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<HttpRequestException>(() => s.Screen(new System.Text.Json.Nodes.JsonObject { ["paymentId"] = "P-1" }));
        Assert.Equal(3, screening.Calls);
        Assert.DoesNotContain(breakers.All, x => x.Key.Tenant == tenant && x.Key.Port == ScreeningAdapters.Sanctions && x.State != BreakerState.Closed);
    }

    [Fact]
    public async Task A_webhook_that_fails_opens_its_breaker_then_one_trial_closes_it_once_the_endpoint_answers()
    {
        var tenant = $"bank-wh-{Guid.NewGuid():N}"[..16];
        var gw = new Gateway(HttpStatusCode.BadGateway, HttpStatusCode.OK);
        var f = FactsOutAdapters.Create(Bind(FactsOutAdapters.Port, "webhook", new() { ["baseUrl"] = "https://events.bank-breaker.invalid/", ["breakerFailures"] = "1", ["breakerOpenSeconds"] = "1" }, tenant), new HttpClient(gw));
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Publish("k-1", new System.Text.Json.Nodes.JsonObject { ["subject"] = "P-1" }));
        await Assert.ThrowsAsync<CircuitOpenException>(() => f.Publish("k-1", new System.Text.Json.Nodes.JsonObject { ["subject"] = "P-1" }));
        Assert.Equal(1, gw.Calls);
        await Task.Delay(1100);
        Assert.Equal("k-1", await f.Publish("k-1", new System.Text.Json.Nodes.JsonObject { ["subject"] = "P-1" }));
        Assert.Equal(2, gw.Calls);
        Assert.Contains(CircuitBreakers.Shared.All, x => x.Key.Tenant == tenant && x.State == BreakerState.Closed);
    }
}
