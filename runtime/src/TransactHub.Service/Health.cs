using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using TransactHub.Api;
using TransactHub.Engine.Flowable;
using TransactHub.Operations;
using TransactHub.Ports;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Service;

/// <summary>One probe's answer: ok, warn or fail, with the error code of the dictionary when it is not ok (R-09-39).</summary>
public sealed record ProbeResult(string Name, string Group, string Status, string Detail, DateTimeOffset At, double Ms, string? Code = null, string? Version = null, string? Dashboard = null);

/// <summary>A cheap check of one component. It never sends a payment or a credential's value.</summary>
public interface IProbe
{
    string Name { get; }
    string Group { get; }
    string? Dashboard { get; }
    Task<(string status, string detail, string? code, string? version)> Check(CancellationToken ct);
}

/// <summary>
/// The runtime's health, as the service console shows it (spec R-09-39): the engine hosts and servers, the store and the read
/// model (its lag), each port's cheap probe, the workers and their versions, the signing keys and certificates with their
/// expiry, and disk; the latest answer of each and a short history, sampled by <see cref="HealthSampler"/>.
/// </summary>
public sealed class HealthMonitor(IEnumerable<IProbe> probes, TimeProvider clock, int historyLength = 60)
{
    readonly List<IProbe> probes = probes.ToList();
    readonly ConcurrentDictionary<string, LinkedList<ProbeResult>> history = new();
    public IReadOnlyList<IProbe> Probes => probes;
    public DateTimeOffset? SampledAt { get; private set; }

    public async Task<IReadOnlyList<ProbeResult>> Sample(CancellationToken ct = default)
    {
        var results = await Task.WhenAll(probes.Select(async p =>
        {
            var sw = Stopwatch.StartNew();
            (string status, string detail, string? code, string? version) r;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                r = await p.Check(cts.Token);
            }
            catch (Exception e) { r = ("fail", TransactHub.Telemetry.Masking.Scrub(e.Message), ErrorCodes.ProbeFailed, null); }
            var res = new ProbeResult(p.Name, p.Group, r.status, r.detail, clock.GetUtcNow(), Math.Round(sw.Elapsed.TotalMilliseconds, 1), r.code, r.version, p.Dashboard);
            var h = history.GetOrAdd(p.Name, _ => new LinkedList<ProbeResult>());
            lock (h) { h.AddLast(res); while (h.Count > historyLength) h.RemoveFirst(); }
            return res;
        }));
        SampledAt = clock.GetUtcNow();
        return results;
    }

    public IReadOnlyList<ProbeResult> History(string name)
    {
        if (!history.TryGetValue(name, out var h)) return [];
        lock (h) return h.ToList();
    }

    public IReadOnlyList<ProbeResult> Latest() => probes.Select(p => History(p.Name).LastOrDefault()).OfType<ProbeResult>().ToList();
}

/// <summary>Samples every probe on a period (Service:Health:EverySeconds, default 30).</summary>
public sealed class HealthSampler(HealthMonitor m, IConfiguration cfg, TransactHub.Operations.RuntimeGauges? gauges = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var every = TimeSpan.FromSeconds(int.TryParse(cfg["Service:Health:EverySeconds"], out var s) ? s : 30);
        while (!ct.IsCancellationRequested)
        {
            try { await m.Sample(ct); } catch (OperationCanceledException) { break; } catch { /* each probe reports its own failure */ }
            // The gauges the alert rules read (R-09-72), on the same beat.
            try { if (gauges is not null) await gauges.Refresh(ct); } catch (OperationCanceledException) { break; } catch { /* the next beat reads again */ }
            try { await Task.Delay(every, ct); } catch (OperationCanceledException) { break; }
        }
    }
}

sealed class FuncProbe(string name, string group, string? dashboard, Func<CancellationToken, Task<(string, string, string?, string?)>> check) : IProbe
{
    public string Name => name;
    public string Group => group;
    public string? Dashboard => dashboard;
    public Task<(string status, string detail, string? code, string? version)> Check(CancellationToken ct) => check(ct);
}

/// <summary>The probes of an installation, from its configuration and what the host already knows.</summary>
public static class Probes
{

    public static IProbe Store(StoreOptions store, string? dashboard) => new FuncProbe("store", "databases", dashboard, async ct =>
    {
        await using var db = StoreDbContext.Open(store);
        return await db.Database.CanConnectAsync(ct) ? ("ok", $"{store.Provider} answers", null, null) : ("fail", "the store does not answer", ErrorCodes.StoreUnavailable, null);
    });

    public static IProbe ReadModel(Projector p, string? dashboard, double warnSeconds = 30) => new FuncProbe("read-model", "databases", dashboard, async ct =>
    {
        var lag = await p.Lag(ct);
        var detail = $"{lag.Behind} outbox rows behind, oldest {Math.Round(lag.OldestSeconds)} s; {lag.Applied} facts applied";
        return lag.Behind == 0 ? ("ok", detail, null, null) : lag.OldestSeconds > warnSeconds ? ("fail", detail, ErrorCodes.ReadModelLagging, null) : ("warn", detail, ErrorCodes.ReadModelLagging, null);
    });

    public static IProbe Source(IPackageSource src) => new FuncProbe("package-source", "sources", null, async ct =>
    {
        try { var v = await src.List(ct); return ("ok", $"{v.Count} signed versions offered", null, null); }
        catch (Exception e) when (e is PackageRejected or HttpRequestException or SecretUnavailable) { return ("fail", e.Message, ErrorCodes.SourceUnreachable, null); }
    });

    public static IProbe Http(string name, string group, string url, string? dashboard, HttpClient http, bool version = false, string failCode = ErrorCodes.ProbeFailed) => new FuncProbe(name, group, dashboard, async ct =>
    {
        HttpResponseMessage response;
        // A host that does not answer fails the probe with its code (R-09-244, Issue I-352), not the health page.
        try { response = await http.GetAsync(url, ct); }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return ("fail", $"{name} does not answer", failCode, null);
        }
        using var r = response;
        string? v = null;
        if (version && r.IsSuccessStatusCode)
        {
            try { var j = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct)); v = (string?)j?["version"] ?? (string?)j?["build"]; } catch { /* no version */ }
        }
        return r.IsSuccessStatusCode ? ("ok", $"HTTP {(int)r.StatusCode}", null, v) : ("fail", $"HTTP {(int)r.StatusCode}", failCode, v);
    });

    /// <summary>
    /// The runtime's own hosts (R-09-244, Issue I-352): the native host's and the activity workers' health (<c>Service:Hosts:NativeHost</c>,
    /// <c>Service:Hosts:Workers</c>, URLs; the native host's from <c>TH_NATIVE_HOST</c> where it names none) and the engine host
    /// (<c>Service:Hosts:EngineHost</c>, host and port, as its health check only connects: its routes need a token). Each fails
    /// with its own code and runbook.
    /// </summary>
    public static IEnumerable<IProbe> RuntimeHosts(IConfiguration cfg, HttpClient http)
    {
        var dash = cfg["Service:Dashboards:Engines"];
        var native = cfg["Service:Hosts:NativeHost"] ?? (cfg["TH_NATIVE_HOST"] is { Length: > 0 } n ? $"{n.TrimEnd('/')}/health" : null);
        if (native is { Length: > 0 }) yield return Http("native-host", "hosts", native, dash, http, failCode: ErrorCodes.NativeHostDown);
        foreach (var w in (cfg["Service:Hosts:Workers"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Http($"workers:{new Uri(w).Authority}", "hosts", w, dash, http, failCode: ErrorCodes.WorkersDown);
        foreach (var e in (cfg["Service:Hosts:EngineHost"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Tcp($"engine-host:{e}", "hosts", e, dash, ErrorCodes.EngineHostDown);
    }

    public static IProbe Tcp(string name, string group, string hostPort, string? dashboard, string failCode = ErrorCodes.ProbeFailed) => new FuncProbe(name, group, dashboard, async ct =>
    {
        var i = hostPort.LastIndexOf(':');
        using var c = new TcpClient();
        try { await c.ConnectAsync(hostPort[..i], int.Parse(hostPort[(i + 1)..]), ct); return ("ok", $"{hostPort} accepts connections", null, null); }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { return ("fail", $"{hostPort} does not accept connections", failCode, null); }
    });

    public static IProbe Flowable(FlowableRest f, string? dashboard) => new FuncProbe("flowable", "engines", dashboard, async ct =>
    {
        try { var e = await f.Engine(); return ("ok", $"engine {(string?)e["name"]} answers", null, (string?)e["version"]); }
        catch (Exception e) when (e is FlowableException or HttpRequestException) { return ("fail", e.Message, "flowable.Unreachable", null); }
    });

    /// <summary>The trusted signers: each key, retired or revoked; a key retiring within the warning window warns.</summary>
    public static IProbe Signers(string setting, ISecretResolver secrets, TimeProvider clock, TimeSpan warn) => new FuncProbe("trusted-signers", "keys", null, async ct =>
    {
        TrustedSigners t;
        try { t = await TrustedSigners.Load(setting, secrets, ct); }
        catch (Exception e) when (e is SignatureRejected or SecretUnavailable) { return ("fail", e.Message, ErrorCodes.TrustListInvalid, null); }
        var now = clock.GetUtcNow();
        var active = t.Keys.Values.Where(k => k.RetiredAt is null || k.RetiredAt > now).ToList();
        var soon = active.Where(k => k.RetiredAt is { } r && r - now < warn).ToList();
        var detail = $"{active.Count} signing key(s) in use, {t.Keys.Count - active.Count} retired, {t.Revoked.Count} revoked" +
            (soon.Count > 0 ? $"; {string.Join(", ", soon.Select(k => $"{k.KeyId[..8]} retires {k.RetiredAt:yyyy-MM-dd}"))}" : "");
        if (active.Count == 0) return ("fail", detail, ErrorCodes.UntrustedKey, null);
        return soon.Count > 0 ? ("warn", detail, ErrorCodes.CertificateExpiring, null) : ("ok", detail, null, null);
    });

    public static IProbe Certificate(string name, string path, TimeProvider clock, TimeSpan warn) => new FuncProbe($"certificate:{name}", "keys", null, ct =>
    {
        using var c = X509Certificate2.CreateFromPem(File.ReadAllText(path));
        var left = c.NotAfter.ToUniversalTime() - clock.GetUtcNow().UtcDateTime;
        var detail = $"expires {c.NotAfter.ToUniversalTime():yyyy-MM-dd} ({Math.Floor(left.TotalDays)} days)";
        return Task.FromResult<(string, string, string?, string?)>(left <= TimeSpan.Zero ? ("fail", detail, ErrorCodes.CertificateExpiring, null)
            : left < warn ? ("warn", detail, ErrorCodes.CertificateExpiring, null) : ("ok", detail, null, null));
    });

    public static IProbe Disk(string path, double warnFreeRatio = 0.1) => new FuncProbe($"disk:{path}", "disk", null, _ =>
    {
        var d = new DriveInfo(Path.GetFullPath(path));
        var ratio = d.TotalSize == 0 ? 1 : (double)d.AvailableFreeSpace / d.TotalSize;
        var detail = $"{d.AvailableFreeSpace / (1024 * 1024 * 1024.0):0.0} GB free of {d.TotalSize / (1024 * 1024 * 1024.0):0.0} GB";
        return Task.FromResult<(string, string, string?, string?)>(ratio < warnFreeRatio / 2 ? ("fail", detail, ErrorCodes.DiskLow, null) : ratio < warnFreeRatio ? ("warn", detail, ErrorCodes.DiskLow, null) : ("ok", detail, null, null));
    });

    /// <summary>
    /// The circuit breakers of the production adapters (R-09-155, R-09-156), as the hosts' journals in the store last recorded each:
    /// an open breaker fails the probe, a half-open one warns, and the detail names each by port, adapter and target (hosts only,
    /// never a credential). With none open the probe is ok.
    /// </summary>
    public static IProbe Breakers(StoreOptions store, string? tenant, string? dashboard) => new FuncProbe("circuit-breakers", "ports", dashboard, async ct =>
    {
        var all = await TransactHub.Operations.Breakers.Latest(store, tenant, ct);
        var open = all.Where(b => b.State == "open").ToList();
        var half = all.Where(b => b.State == "half-open").ToList();
        string Name(TransactHub.Operations.Breakers.Seen b) => $"{b.Port} {b.Adapter} {b.Target} ({b.Tenant}) {b.State} since {b.At:yyyy-MM-dd HH:mm:ss}Z after {b.Failures} failure(s), seen by {b.Host}";
        var detail = open.Count + half.Count == 0 ? $"{all.Count} breaker(s) known, none open" : string.Join("; ", open.Concat(half).Select(Name));
        return open.Count > 0 ? ("fail", detail, TransactHub.Ports.CircuitBreakers.OpenCode, null) : half.Count > 0 ? ("warn", detail, ErrorCodes.ProbeFailed, null) : ("ok", detail, null, null);
    });

    /// <summary>
    /// The probes an installation configures, <c>Service:Probes:&lt;n&gt;:Name|Group|Kind|Target|Dashboard</c> (kind <c>http</c>,
    /// <c>tcp</c> or <c>worker</c>, which also reads the worker's version), and the ports the bank's configuration release in
    /// force binds (R-09-112), as the service console starts: each binding with a <c>baseUrl</c> is probed by a TCP connection to
    /// its host, never by a request.
    /// </summary>
    public static IEnumerable<IProbe> Configured(IConfiguration cfg, HttpClient http, PortBindings? bindings = null)
    {
        foreach (var s in cfg.GetSection("Service:Probes").GetChildren())
        {
            var name = s["Name"] ?? s.Key;
            var group = s["Group"] ?? "ports";
            var target = s["Target"] ?? "";
            var dash = s["Dashboard"];
            yield return (s["Kind"] ?? "http") switch
            {
                "tcp" => Tcp(name, group, target, dash, group == "engines" && name.StartsWith("temporal") ? "temporal.ServerUnreachable" : ErrorCodes.ProbeFailed),
                "worker" => Http(name, "workers", target, dash, http, version: true),
                _ => Http(name, group, target, dash, http),
            };
        }
        if (bindings is null) yield break;
        IReadOnlyList<PortBinding> all;
        try { all = bindings.All; } catch (PortBindingException) { yield break; }
        foreach (var g in all.GroupBy(x => (x.Tenant, x.Port)))
        {
            PortBinding now;
            try { now = bindings.InForce(g.Key.Tenant, g.Key.Port, DateTimeOffset.UtcNow); } catch (PortBindingException) { continue; }
            if (now.Settings.TryGetValue("baseUrl", out var url) && Uri.TryCreate(url, UriKind.Absolute, out var u))
                yield return Tcp($"port:{now.Tenant}:{now.Port}", "ports", $"{u.Host}:{u.Port}", cfg["Service:Dashboards:Ports"]);
        }
    }
}
