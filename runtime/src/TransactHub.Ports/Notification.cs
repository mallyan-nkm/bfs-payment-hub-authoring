using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using MQTTnet;
using MQTTnet.Protocol;
using TransactHub.Security;

namespace TransactHub.Ports;

// The platform's notification port, p.port.notification.v1 (R-09-150, owner's decisions of 23 November 2026, Issue I-182,
// docs/OWNER-ACTIONS.md A5): where alerts for the bank's operators go, the operations console's turnaround breaches among them
// (R-05-80). Operation `alert`, style send. Each bank binds it in its configuration release like any port: `simulator` for tests,
// `log`, `webhook` and `mqtt`. An alert carries ids, codes and periods only, never a party's name, account or amount.

/// <summary>An alert for the bank's operators: what happened (<c>kind</c>, for example <c>tat.breached</c>), to which subject, ids only.</summary>
public sealed record OperatorAlert(string Kind, string Tenant, string SubjectId, string? Scheme, string Title, JsonObject Data)
{
    /// <summary>The alert as every adapter sends it.</summary>
    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind, ["tenant"] = Tenant, ["subject"] = SubjectId, ["scheme"] = Scheme, ["title"] = Title, ["data"] = Data.DeepClone(),
    };
}

public interface INotification
{
    string Adapter { get; }
    /// <summary>Sends the alert; answers the transport's reference. A failure throws (a transport failure is an <see cref="HttpRequestException"/> or an <see cref="IOException"/>).</summary>
    Task<string> Alert(OperatorAlert alert, CancellationToken ct = default);
}

public static class NotificationAdapters
{
    public const string Port = "p.port.notification.v1";

    /// <param name="log">Where the <c>log</c> adapter writes (a warning with ids only).</param>
    public static INotification Create(PortBinding b, Action<string>? log = null, HttpClient? http = null, ISecretResolver? secrets = null) => b.Adapter switch
    {
        SimulatedNotification.Name => new SimulatedNotification(b),
        LogNotification.Name => new LogNotification(log ?? (_ => { })),
        WebhookNotification.Name => new WebhookNotification(b, http ?? new HttpClient(), secrets ?? SecretResolver.FromEnvironment()),
        MqttNotification.Name => new MqttNotification(b, secrets ?? SecretResolver.FromEnvironment()),
        _ => throw new PortBindingException($"No notification adapter is called '{b.Adapter}' (binding for {b.Port}, from {b.ValidFrom:O})"),
    };
}

/// <summary>
/// Keeps what it was asked to send, for tests (the factory's <c>p.sim.notification.v1</c>). Setting <c>answer</c>: <c>accepted</c>
/// (the default) or <c>rejected</c>, which fails every alert as a transport failure does.
/// </summary>
public sealed class SimulatedNotification(PortBinding b) : INotification
{
    public const string Name = "simulator";
    public string Adapter => Name;
    public static readonly ConcurrentQueue<OperatorAlert> Sent = new();
    public static IReadOnlyList<OperatorAlert> SentFor(string tenant) => Sent.Where(a => a.Tenant == tenant).ToList();

    public Task<string> Alert(OperatorAlert alert, CancellationToken ct = default)
    {
        if (b.Setting("answer", "accepted") == "rejected") throw new HttpRequestException($"{b.Port}: the simulator refuses the alert ({alert.Kind} {alert.SubjectId})");
        Sent.Enqueue(alert with { Data = (JsonObject)alert.Data.DeepClone() });
        return Task.FromResult($"sim-{alert.Kind}-{alert.SubjectId}");
    }
}

/// <summary>A warning in the host's log, ids only: the platform's way when a bank sends its alerts nowhere else.</summary>
public sealed class LogNotification(Action<string> log) : INotification
{
    public const string Name = "log";
    public string Adapter => Name;
    public Task<string> Alert(OperatorAlert a, CancellationToken ct = default)
    {
        log($"{a.Kind} {a.SubjectId} ({a.Tenant}, {a.Scheme}): {a.Title}");
        return Task.FromResult($"log-{a.Kind}-{a.SubjectId}");
    }
}

/// <summary>
/// The bank's alert endpoint: <c>POST {baseUrl}{alertPath}</c> (default <c>/alerts</c>) with the alert as JSON, the header
/// <c>X-TH-Signature</c> (HMAC-SHA256 of the body with the key <c>signingKeyRef</c> names, by reference) and the credential by
/// <c>secretRef</c> as a bearer token; <c>timeoutSeconds</c> (default 10). Behind the endpoint's circuit breaker (R-09-155).
/// </summary>
public sealed class WebhookNotification(PortBinding b, HttpClient http, ISecretResolver secrets) : INotification
{
    public const string Name = "webhook";
    public string Adapter => Name;
    public async Task<string> Alert(OperatorAlert alert, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(alert.ToJson().ToJsonString());
        using var m = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(b.Required("baseUrl")), b.Setting("alertPath", "/alerts")))
        {
            Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/json") } },
        };
        if (b.Setting("signingKeyRef", "") is { Length: > 0 } sk) m.Headers.Add("X-TH-Signature", FactsOutAdapters.Sign(bytes, (await secrets.Resolve(sk, ct)).Reveal()));
        if (b.Setting("secretRef", "") is { Length: > 0 } r) m.Headers.Authorization = new("Bearer", (await secrets.Resolve(r, ct)).Reveal());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(double.Parse(b.Setting("timeoutSeconds", "10"), CultureInfo.InvariantCulture)));
        using var res = await GuardedHttp.Send(http, b, m, timeout.Token, ct);
        if ((int)res.StatusCode >= 500 || res.StatusCode == HttpStatusCode.TooManyRequests) throw new HttpRequestException($"{b.Port}: the endpoint answered {(int)res.StatusCode}");
        res.EnsureSuccessStatusCode();
        return $"webhook-{(int)res.StatusCode}";
    }
}

/// <summary>
/// An MQTT broker (R-09-151): settings <c>host</c>, <c>port</c> (default 8883 with TLS, 1883 without), <c>topic</c> (a template,
/// default <c>transacthub/{tenant}/alerts/{kind}</c>, with <c>{tenant}</c>, <c>{kind}</c> and <c>{scheme}</c>), <c>qos</c> (0, 1 or 2;
/// default 1), <c>retain</c> (default false), <c>tls</c> (default true), <c>caRef</c> (a CA certificate in PEM, by reference, for a
/// broker whose certificate a private CA signed), <c>clientId</c> (default <c>transacthub-{tenant}-alerts</c>), <c>timeoutSeconds</c>
/// (default 10); the login by <c>secretRef</c> as <c>user:password</c>, else none. One connection per alert: alerts are few. With
/// QoS 1 or 2 the alert is sent once the broker acknowledged it. Behind the broker's circuit breaker (R-09-155).
/// </summary>
public sealed class MqttNotification(PortBinding b, ISecretResolver secrets) : INotification
{
    public const string Name = "mqtt";
    public string Adapter => Name;
    static readonly MqttClientFactory Factory = new();

    public bool Tls => bool.Parse(b.Setting("tls", "true"));
    public int BrokerPort => int.Parse(b.Setting("port", Tls ? "8883" : "1883"), CultureInfo.InvariantCulture);

    public MqttQualityOfServiceLevel Qos => b.Setting("qos", "1") switch
    {
        "0" => MqttQualityOfServiceLevel.AtMostOnce,
        "1" => MqttQualityOfServiceLevel.AtLeastOnce,
        "2" => MqttQualityOfServiceLevel.ExactlyOnce,
        var q => throw new PortBindingException($"The {Name} binding of {b.Port} for {b.Tenant}: qos is '{q}', not 0, 1 or 2"),
    };

    /// <summary>The topic of an alert: the template with its tenant, kind and scheme; a character MQTT reserves is refused.</summary>
    public string TopicOf(OperatorAlert a)
    {
        static string Part(string? s) => (s ?? "none").Replace('/', '-').Replace('+', '-').Replace('#', '-');
        var t = b.Setting("topic", "transacthub/{tenant}/alerts/{kind}")
            .Replace("{tenant}", Part(a.Tenant), StringComparison.Ordinal).Replace("{kind}", Part(a.Kind), StringComparison.Ordinal).Replace("{scheme}", Part(a.Scheme), StringComparison.Ordinal);
        if (t.Length == 0 || t.Contains('+') || t.Contains('#') || t.Contains('{')) throw new PortBindingException($"The {Name} binding of {b.Port} for {b.Tenant}: topic '{t}' is not a topic to publish to");
        return t;
    }

    public async Task<string> Alert(OperatorAlert alert, CancellationToken ct = default)
    {
        var host = b.Required("host");
        var topic = TopicOf(alert);
        var qos = Qos;
        var timeout = TimeSpan.FromSeconds(double.Parse(b.Setting("timeoutSeconds", "10"), CultureInfo.InvariantCulture));
        var builder = new MqttClientOptionsBuilder().WithTcpServer(host, BrokerPort).WithTimeout(timeout)
            .WithClientId(b.Setting("clientId", $"transacthub-{b.Tenant}-alerts")).WithCleanSession(true);
        if (b.Setting("secretRef", "") is { Length: > 0 } r)
        {
            var login = (await secrets.Resolve(r, ct)).Reveal();
            var i = login.IndexOf(':');
            if (i <= 0) throw new PortBindingException($"The {Name} binding of {b.Port} for {b.Tenant}: its secretRef holds no user:password");
            builder = builder.WithCredentials(login[..i], login[(i + 1)..]);
        }
        if (Tls)
        {
            X509Certificate2? ca = b.Setting("caRef", "") is { Length: > 0 } caRef ? X509Certificate2.CreateFromPem((await secrets.Resolve(caRef, ct)).Reveal()) : null;
            builder = builder.WithTlsOptions(o =>
            {
                o.UseTls(true).WithTargetHost(host);
                if (ca is not null) o.WithCertificateValidationHandler(e => TrustedBy(ca, e.Certificate, e.SslPolicyErrors));
            });
        }
        var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(Encoding.UTF8.GetBytes(alert.ToJson().ToJsonString()))
            .WithQualityOfServiceLevel(qos).WithRetainFlag(bool.Parse(b.Setting("retain", "false"))).WithContentType("application/json").Build();
        return await CircuitBreakers.Shared.For(b, $"{host}:{BrokerPort}").Run(async () =>
        {
            using var client = Factory.CreateMqttClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await client.ConnectAsync(builder.Build(), cts.Token);
                var res = await client.PublishAsync(message, cts.Token);
                if (!res.IsSuccess) throw new IOException($"{b.Port}: the broker refused the alert on {topic} ({res.ReasonCode})");
                await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
                return $"mqtt:{topic}";
            }
            catch (Exception e) when (e is not IOException && e is not OperationCanceledException && e is not PortBindingException)
            {
                // The client's own failures (connecting, the broker's refusal of the login) are transport failures of the broker.
                throw new IOException($"{b.Port}: MQTT to {host}:{BrokerPort} failed: {e.Message}", e);
            }
        }, ct: ct);
    }

    /// <summary>A broker certificate is trusted when it chains to the binding's CA (and names the host), or the system trusts it.</summary>
    static bool TrustedBy(X509Certificate2 ca, X509Certificate? cert, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (cert is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(cert as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(cert.GetRawCertData()));
    }
}
