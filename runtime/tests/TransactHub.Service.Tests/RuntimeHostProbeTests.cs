using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace TransactHub.Service.Tests;

/// <summary>
/// R-09-244 (Issue I-352): the service console's health probes the runtime's own hosts: the native host's and the activity workers'
/// health (HTTP) and the engine host (it only connects: its routes need a token). A host that answers is ok; one that does not fails
/// with its own code, which the error dictionary has, and the probe does not throw.
/// </summary>
public sealed class RuntimeHostProbeTests
{
    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public async Task The_native_host_the_workers_and_the_engine_host_are_probed_each_with_its_code()
    {
        // A native host that answers its health; workers and an engine host that do not.
        var up = FreePort();
        using var native = new HttpListener();
        native.Prefixes.Add($"http://127.0.0.1:{up}/");
        native.Start();
        _ = Task.Run(async () =>
        {
            while (native.IsListening)
            {
                HttpListenerContext c;
                try { c = await native.GetContextAsync(); } catch { return; }
                var b = Encoding.UTF8.GetBytes("{\"contract\":\"2.0.0\"}");
                await c.Response.OutputStream.WriteAsync(b);
                c.Response.Close();
            }
        });
        var down = FreePort();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Service:Hosts:NativeHost"] = $"http://127.0.0.1:{up}/health",
            ["Service:Hosts:Workers"] = $"http://127.0.0.1:{down}/health",
            ["Service:Hosts:EngineHost"] = $"127.0.0.1:{down}",
        }).Build();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var probes = Probes.RuntimeHosts(cfg, http).ToList();
        Assert.Equal(["native-host", $"workers:127.0.0.1:{down}", $"engine-host:127.0.0.1:{down}"], probes.Select(p => p.Name));
        var results = new List<(string status, string? code)>();
        foreach (var p in probes) { var r = await p.Check(CancellationToken.None); results.Add((r.status, r.code)); }
        Assert.Equal([("ok", null), ("fail", ErrorCodes.WorkersDown), ("fail", ErrorCodes.EngineHostDown)], results);
        // A native host that does not answer fails with its own code; with no setting, TH_NATIVE_HOST names it.
        native.Stop();
        var gone = await Probes.RuntimeHosts(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TH_NATIVE_HOST"] = $"http://127.0.0.1:{up}" }).Build(), http).Single().Check(CancellationToken.None);
        Assert.Equal(("fail", ErrorCodes.NativeHostDown), (gone.status, gone.code));
        // Nothing named, nothing probed.
        Assert.Empty(Probes.RuntimeHosts(new ConfigurationBuilder().Build(), http));
    }
}
