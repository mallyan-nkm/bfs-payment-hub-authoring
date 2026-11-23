using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Engines;
using TransactHub.Store;

namespace TransactHub.EngineHost;

/// <summary>The store the hosts read the distributions' states from and record pins and ends in.</summary>
public static class HostStore
{
    public static StoreOptions? FromEnvironment()
    {
        var p = Environment.GetEnvironmentVariable("TH_STORE_PROVIDER");
        var c = Environment.GetEnvironmentVariable("TH_STORE_CONNECTION");
        return string.IsNullOrEmpty(p) || string.IsNullOrEmpty(c) ? null : new StoreOptions(Enum.Parse<StoreProvider>(p, true), c);
    }

    public static TimeSpan Duration(string name, TimeSpan dflt) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? System.Xml.XmlConvert.ToTimeSpan(v) : dflt;
}

/// <summary>
/// A small JSON endpoint on a loopback address: the hosts serve only the supervisor and the runtime on the same machine, as the
/// native host does without a bank's identity provider (R-08-17). With a token (R-08-22, Issue I-121) every call must carry it
/// as <c>Authorization: Bearer</c>; a call without it is answered 401 and goes no further.
/// </summary>
public sealed class Http(Func<string, string, JsonObject?, Task<(int status, JsonNode? body)>> route, string? token = null) : IDisposable
{
    /// <summary>The token this process sends on its own calls to the hosts (the supervisor's to its hosts).</summary>
    public static string? CallerToken { get; set; }

    readonly HttpListener listener = new();
    public string Url { get; private set; } = "";

    public void Start(string urls)
    {
        var u = new Uri(urls);
        if (!IPAddress.IsLoopback(IPAddress.TryParse(u.Host, out var ip) ? ip : IPAddress.None) && u.Host != "localhost")
            throw new InvalidOperationException($"the engine host answers on a loopback address only, not {u.Host}");
        var port = u.Port;
        if (port == 0)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
        }
        Url = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add($"{Url}/");
        listener.Start();
    }

    public async Task Serve(CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext c;
            try { c = await listener.GetContextAsync(); }
            catch when (ct.IsCancellationRequested) { return; }
            catch (HttpListenerException) { return; }
            _ = Task.Run(async () =>
            {
                int status;
                JsonNode? body;
                try
                {
                    if (token is not null && !TransactHub.Security.HostToken.Accepts(token, c.Request.Headers["Authorization"]))
                        throw new HostUnauthorised();
                    JsonObject? input = null;
                    if (c.Request.HasEntityBody)
                    {
                        using var r = new StreamReader(c.Request.InputStream, Encoding.UTF8);
                        var text = await r.ReadToEndAsync();
                        if (text.Length > 0) input = JsonNode.Parse(text) as JsonObject;
                    }
                    (status, body) = await route(c.Request.HttpMethod, c.Request.Url!.AbsolutePath, input);
                }
                catch (HostUnauthorised) { (status, body) = (401, new JsonObject { ["error"] = "Send the engine host's token: Authorization: Bearer (R-08-22)" }); }
                catch (DistributionRefused e) { (status, body) = (409, new JsonObject { ["error"] = e.Message }); }
                catch (Exception e) { (status, body) = (500, new JsonObject { ["error"] = e.Message }); }
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(body is null ? "{}" : J.Str(body));
                    c.Response.StatusCode = status;
                    c.Response.ContentType = "application/json";
                    await c.Response.OutputStream.WriteAsync(bytes);
                    c.Response.Close();
                }
                catch (Exception) { }
            });
        }
    }

    public void Dispose() { try { listener.Close(); } catch { } }

    static readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static async Task<(int status, JsonObject body)> Call(string method, string url, JsonObject? body = null)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (CallerToken is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CallerToken);
        if (body is not null) req.Content = new StringContent(J.Str(body), Encoding.UTF8, "application/json");
        using var res = await client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return ((int)res.StatusCode, string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? new JsonObject());
    }
}

/// <summary>A call without the engine host's token (R-08-22).</summary>
sealed class HostUnauthorised : Exception;
