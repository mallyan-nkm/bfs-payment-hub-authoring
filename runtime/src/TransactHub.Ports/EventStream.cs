using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using TransactHub.Security;

namespace TransactHub.Ports;

// The platform's event-streaming port, p.port.event-stream.v1 (R-09-152 to R-09-154, owner's decisions of 23 November 2026,
// docs/OWNER-ACTIONS.md A5): the payment events the store's transactional outbox announces, streamed out as each fact is written.
// An event is the fact's reference only: its subject, its number on the subject's chain, its kind, code and time. A reader who
// needs more reads the fact itself through the runtime's APIs, masked as their views declare. Each bank binds the port in its
// configuration release: `simulator` for tests, `kafka` for a bank.

/// <summary>
/// One payment event, as the outbox announced it (R-09-152): <c>position</c> is the outbox row's number, so a reader can tell a
/// repeat (delivery is at least once) from a new event; <c>subject</c> is the event's key, so a subject's events stay in order.
/// </summary>
public sealed record PaymentEvent(long Position, string Tenant, string Subject, string SubjectKind, long Seq, string Kind, string Code, DateTimeOffset At)
{
    public const string EventKind = "fact";

    public JsonObject ToJson() => new()
    {
        ["event"] = EventKind, ["position"] = Position, ["tenant"] = Tenant, ["subject"] = Subject, ["subjectKind"] = SubjectKind,
        ["seq"] = Seq, ["kind"] = Kind, ["code"] = Code, ["at"] = At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    };
}

public interface IEventStream : IDisposable
{
    string Adapter { get; }
    /// <summary>
    /// Publishes the events in their order and returns once the platform holds every one; a failure throws and the caller sends the
    /// batch again (at least once). Events of one subject are kept in the order given.
    /// </summary>
    Task Publish(IReadOnlyList<PaymentEvent> events, CancellationToken ct = default);
}

public static class EventStreamAdapters
{
    public const string Port = "p.port.event-stream.v1";

    public static IEventStream Create(PortBinding b, ISecretResolver? secrets = null) => b.Adapter switch
    {
        SimulatedEventStream.Name => new SimulatedEventStream(b),
        KafkaEventStream.Name => new KafkaEventStream(b, secrets ?? SecretResolver.FromEnvironment()),
        _ => throw new PortBindingException($"No event-stream adapter is called '{b.Adapter}' (binding for {b.Port}, from {b.ValidFrom:O})"),
    };
}

/// <summary>
/// Keeps every event it was given, for tests (the factory's <c>p.sim.event-stream.v1</c>). Setting <c>answer</c>: <c>accepted</c>
/// (the default) or <c>rejected</c>, which fails every batch as a platform that does not answer does.
/// </summary>
public sealed class SimulatedEventStream(PortBinding b) : IEventStream
{
    public const string Name = "simulator";
    public string Adapter => Name;
    public static readonly ConcurrentQueue<PaymentEvent> Published = new();
    public static IReadOnlyList<PaymentEvent> For(string tenant) => Published.Where(e => e.Tenant == tenant).ToList();

    public Task Publish(IReadOnlyList<PaymentEvent> events, CancellationToken ct = default)
    {
        if (b.Setting("answer", "accepted") == "rejected") throw new HttpRequestException($"{b.Port}: the simulator refuses {events.Count} event(s)");
        foreach (var e in events) Published.Enqueue(e);
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

/// <summary>
/// A Kafka topic (R-09-154): settings <c>bootstrapServers</c>, <c>topic</c>, <c>acks</c> (<c>all</c>, the default and the only value
/// an idempotent producer takes, or <c>1</c> or <c>0</c> with <c>idempotent: false</c>), <c>idempotent</c> (default true: no event is
/// written twice by a retry and a subject's events keep their order), <c>securityProtocol</c> (<c>plaintext</c>, <c>ssl</c>,
/// <c>sasl_plaintext</c> or <c>sasl_ssl</c>; default <c>sasl_ssl</c>), <c>saslMechanism</c> (default <c>SCRAM-SHA-512</c>), the login
/// by <c>secretRef</c> as <c>user:password</c>, <c>caRef</c> (a CA certificate in PEM, by reference), <c>clientId</c> and
/// <c>timeoutSeconds</c> (default 30). The key of each message is the event's subject, so a payment's events go to one partition in
/// order; the value is the event as JSON. Behind the cluster's circuit breaker (R-09-155).
/// </summary>
public sealed class KafkaEventStream : IEventStream
{
    public const string Name = "kafka";
    public string Adapter => Name;
    readonly PortBinding b;
    readonly ISecretResolver secrets;
    readonly SemaphoreSlim gate = new(1, 1);
    IProducer<string, string>? producer;

    public KafkaEventStream(PortBinding b, ISecretResolver secrets)
    {
        this.b = b;
        this.secrets = secrets;
        _ = Config(null);
    }

    public string Topic => b.Required("topic");
    TimeSpan Timeout => TimeSpan.FromSeconds(double.Parse(b.Setting("timeoutSeconds", "30"), CultureInfo.InvariantCulture));

    /// <summary>The producer's settings from the binding; a value the binding may not take is refused here, before any connection.</summary>
    public ProducerConfig Config(string? login)
    {
        var idempotent = bool.Parse(b.Setting("idempotent", "true"));
        var acks = b.Setting("acks", "all") switch
        {
            "all" or "-1" => Acks.All,
            "1" => Acks.Leader,
            "0" => Acks.None,
            var a => throw new PortBindingException($"The {Name} binding of {b.Port} for {b.Tenant}: acks is '{a}', not all, 1 or 0"),
        };
        if (idempotent && acks != Acks.All) throw new PortBindingException($"The {Name} binding of {b.Port} for {b.Tenant}: an idempotent producer needs acks all");
        var protocol = b.Setting("securityProtocol", "sasl_ssl") switch
        {
            "plaintext" => SecurityProtocol.Plaintext,
            "ssl" => SecurityProtocol.Ssl,
            "sasl_plaintext" => SecurityProtocol.SaslPlaintext,
            "sasl_ssl" => SecurityProtocol.SaslSsl,
            var p => throw new PortBindingException($"The {Name} binding of {b.Port} for {b.Tenant}: securityProtocol is '{p}', not plaintext, ssl, sasl_plaintext or sasl_ssl"),
        };
        var c = new ProducerConfig
        {
            BootstrapServers = b.Required("bootstrapServers"),
            ClientId = b.Setting("clientId", $"transacthub-{b.Tenant}-events"),
            Acks = acks,
            EnableIdempotence = idempotent,
            // Order within a partition holds across retries with an idempotent producer and at most five requests in flight.
            MaxInFlight = idempotent ? 5 : 1,
            MessageTimeoutMs = (int)Timeout.TotalMilliseconds,
            SecurityProtocol = protocol,
            LingerMs = 5,
        };
        if (protocol is SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl)
        {
            c.SaslMechanism = b.Setting("saslMechanism", "SCRAM-SHA-512") switch
            {
                "PLAIN" => SaslMechanism.Plain,
                "SCRAM-SHA-256" => SaslMechanism.ScramSha256,
                "SCRAM-SHA-512" => SaslMechanism.ScramSha512,
                var m => throw new PortBindingException($"The {Name} binding of {b.Port} for {b.Tenant}: saslMechanism is '{m}', not PLAIN, SCRAM-SHA-256 or SCRAM-SHA-512"),
            };
            if (login is not null)
            {
                var i = login.IndexOf(':');
                if (i <= 0) throw new PortBindingException($"The {Name} binding of {b.Port} for {b.Tenant}: its secretRef holds no user:password");
                (c.SaslUsername, c.SaslPassword) = (login[..i], login[(i + 1)..]);
            }
        }
        return c;
    }

    async Task<IProducer<string, string>> Producer(CancellationToken ct)
    {
        if (producer is not null) return producer;
        var login = b.Setting("secretRef", "") is { Length: > 0 } r ? (await secrets.Resolve(r, ct)).Reveal() : null;
        var c = Config(login);
        if (b.Setting("caRef", "") is { Length: > 0 } ca) c.SslCaPem = (await secrets.Resolve(ca, ct)).Reveal();
        return producer = new ProducerBuilder<string, string>(c).SetLogHandler((_, _) => { /* the client's own log names hosts only; the adapter reports its failures */ }).Build();
    }

    public async Task Publish(IReadOnlyList<PaymentEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return;
        await gate.WaitAsync(ct);
        try
        {
            var topic = Topic;
            var target = b.Required("bootstrapServers").Split(',')[0].Trim();
            await CircuitBreakers.Shared.For(b, target).Run(async () =>
            {
                var p = await Producer(ct);
                // Each message is handed to the producer in order before any is awaited, so a subject's events keep their order.
                var sent = events.Select(e => p.ProduceAsync(topic, new Message<string, string> { Key = e.Subject, Value = e.ToJson().ToJsonString() }, ct)).ToList();
                try { await Task.WhenAll(sent); }
                catch (ProduceException<string, string> x)
                {
                    throw new IOException($"{b.Port}: Kafka at {target} did not take every event: {x.Error.Code} {x.Error.Reason}", x);
                }
            }, ct: ct);
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        try { producer?.Flush(TimeSpan.FromSeconds(5)); } catch { /* closing */ }
        producer?.Dispose();
        producer = null;
    }
}
