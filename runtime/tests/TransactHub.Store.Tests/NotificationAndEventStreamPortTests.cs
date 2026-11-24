using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Ports;
using TransactHub.Security;
using Xunit;

namespace TransactHub.Store.Tests;

/// <summary>
/// The platform's notification port (p.port.notification.v1, R-09-150, R-09-151) and event-streaming port (p.port.event-stream.v1,
/// R-09-152, R-09-154): their simulators, and the settings each production adapter takes or refuses before it connects. The real
/// broker and cluster are in TransactHub.Operations.Tests (MqttNotificationTests, EventStreamTests). Synthetic ids only.
/// </summary>
public sealed class NotificationAndEventStreamPortTests
{
    static PortBinding Bind(string port, string adapter, Dictionary<string, string>? settings = null, string tenant = "bank-ports") =>
        new(tenant, port, adapter, DateTimeOffset.Parse("2026-11-01T00:00:00Z"), settings ?? new());

    static OperatorAlert Alert(string tenant, string subject = "B-000001") =>
        new("tat.breached", tenant, subject, "uae.wps", "Turnaround limit P1D (business days) passed", new JsonObject { ["subject"] = subject, ["limit"] = "P1D" });

    sealed class Endpoint : HttpMessageHandler
    {
        public readonly List<(string Url, string Body, Dictionary<string, string> Headers)> Calls = new();
        public HttpStatusCode Answer = HttpStatusCode.Accepted;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add((r.RequestUri!.ToString(), await r.Content!.ReadAsStringAsync(ct), r.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));
            return new HttpResponseMessage(Answer);
        }
    }

    static readonly ISecretResolver Secrets = SecretResolver.FromEnvironment(k => k switch
    {
        "TH_TEST_ALERT_TOKEN" => "alert-token-for-test",
        "TH_TEST_ALERT_SIGN" => "alert-signing-key-for-test",
        _ => null,
    });

    [Fact]
    public async Task The_notification_simulator_keeps_each_alert_and_its_refusing_scenario_fails_as_a_transport_failure()
    {
        var tenant = $"bank-n-{Guid.NewGuid():N}"[..16];
        var sim = NotificationAdapters.Create(Bind(NotificationAdapters.Port, "simulator", tenant: tenant));
        Assert.Equal("simulator", sim.Adapter);
        Assert.Equal("sim-tat.breached-B-000001", await sim.Alert(Alert(tenant)));
        var kept = Assert.Single(SimulatedNotification.SentFor(tenant));
        Assert.Equal("B-000001", kept.SubjectId);
        Assert.Equal("P1D", (string?)kept.Data["limit"]);
        var refusing = NotificationAdapters.Create(Bind(NotificationAdapters.Port, "simulator", new() { ["answer"] = "rejected" }, tenant));
        await Assert.ThrowsAsync<HttpRequestException>(() => refusing.Alert(Alert(tenant, "B-000002")));
        Assert.Single(SimulatedNotification.SentFor(tenant));
        Assert.Throws<PortBindingException>(() => NotificationAdapters.Create(Bind(NotificationAdapters.Port, "pager")));
    }

    [Fact]
    public async Task The_log_adapter_writes_ids_only_and_the_webhook_posts_the_alert_signed_with_its_credential_by_reference()
    {
        var lines = new List<string>();
        await NotificationAdapters.Create(Bind(NotificationAdapters.Port, "log"), lines.Add).Alert(Alert("bank-ports"));
        Assert.Equal("tat.breached B-000001 (bank-ports, uae.wps): Turnaround limit P1D (business days) passed", Assert.Single(lines));

        var endpoint = new Endpoint();
        var hook = NotificationAdapters.Create(Bind(NotificationAdapters.Port, "webhook", new()
        {
            ["baseUrl"] = "https://alerts.bank-ports.invalid/", ["alertPath"] = "/ops/alerts", ["secretRef"] = "env:TH_TEST_ALERT_TOKEN", ["signingKeyRef"] = "env:TH_TEST_ALERT_SIGN",
        }, $"bank-w-{Guid.NewGuid():N}"[..16]), null, new HttpClient(endpoint), Secrets);
        await hook.Alert(Alert("bank-ports"));
        var call = Assert.Single(endpoint.Calls);
        Assert.Equal("https://alerts.bank-ports.invalid/ops/alerts", call.Url);
        Assert.Equal("Bearer alert-token-for-test", call.Headers["Authorization"]);
        Assert.Equal(FactsOutAdapters.Sign(Encoding.UTF8.GetBytes(call.Body), "alert-signing-key-for-test"), call.Headers["X-TH-Signature"]);
        var body = JsonNode.Parse(call.Body)!;
        Assert.Equal("tat.breached", (string?)body["kind"]);
        Assert.Equal("B-000001", (string?)body["subject"]);
        Assert.DoesNotContain("alert-token-for-test", call.Body);
        endpoint.Answer = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<HttpRequestException>(() => hook.Alert(Alert("bank-ports")));
    }

    [Fact]
    public void The_mqtt_adapter_reads_its_topic_template_and_quality_of_service_and_refuses_what_it_cannot_publish()
    {
        var m = (MqttNotification)NotificationAdapters.Create(Bind(NotificationAdapters.Port, "mqtt", new() { ["host"] = "broker.bank-ports.invalid" }));
        Assert.True(m.Tls);
        Assert.Equal(8883, m.BrokerPort);
        Assert.Equal(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, m.Qos);
        Assert.Equal("transacthub/bank-ports/alerts/tat.breached", m.TopicOf(Alert("bank-ports")));
        var plain = (MqttNotification)NotificationAdapters.Create(Bind(NotificationAdapters.Port, "mqtt", new() { ["host"] = "b", ["tls"] = "false", ["qos"] = "2", ["topic"] = "ops/{scheme}/{kind}" }));
        Assert.Equal(1883, plain.BrokerPort);
        Assert.Equal(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce, plain.Qos);
        Assert.Equal("ops/uae.wps/tat.breached", plain.TopicOf(Alert("bank-ports")));
        Assert.Throws<PortBindingException>(() => ((MqttNotification)NotificationAdapters.Create(Bind(NotificationAdapters.Port, "mqtt", new() { ["host"] = "b", ["qos"] = "3" }))).Qos);
        Assert.Throws<PortBindingException>(() => ((MqttNotification)NotificationAdapters.Create(Bind(NotificationAdapters.Port, "mqtt", new() { ["host"] = "b", ["topic"] = "ops/#" }))).TopicOf(Alert("x")));
    }

    [Fact]
    public async Task The_event_stream_simulator_keeps_the_events_in_order_and_its_refusing_scenario_fails_the_whole_batch()
    {
        var tenant = $"bank-e-{Guid.NewGuid():N}"[..16];
        using var sim = EventStreamAdapters.Create(Bind(EventStreamAdapters.Port, "simulator", tenant: tenant));
        var at = DateTimeOffset.Parse("2026-11-24T08:00:00Z");
        PaymentEvent E(long pos, string subject, long seq, string code) => new(pos, tenant, subject, "payment", seq, "step", code, at);
        await sim.Publish([E(1, "P-1", 1, "pass"), E(2, "P-2", 1, "pass"), E(3, "P-1", 2, "posted")]);
        Assert.Equal([1L, 2L, 3L], SimulatedEventStream.For(tenant).Select(e => e.Position));
        var json = SimulatedEventStream.For(tenant)[2].ToJson();
        Assert.Equal(["event", "position", "tenant", "subject", "subjectKind", "seq", "kind", "code", "at"], json.Select(kv => kv.Key));
        Assert.Equal("2026-11-24T08:00:00.0000000+00:00", (string?)json["at"]);
        using var refusing = EventStreamAdapters.Create(Bind(EventStreamAdapters.Port, "simulator", new() { ["answer"] = "rejected" }, tenant));
        await Assert.ThrowsAsync<HttpRequestException>(() => refusing.Publish([E(4, "P-1", 3, "settled")]));
        Assert.Equal(3, SimulatedEventStream.For(tenant).Count);
    }

    [Fact]
    public void The_kafka_adapter_is_an_idempotent_producer_with_acks_all_by_default_and_refuses_settings_that_would_lose_order()
    {
        using var k = (KafkaEventStream)EventStreamAdapters.Create(Bind(EventStreamAdapters.Port, "kafka", new() { ["bootstrapServers"] = "kafka-1.bank-ports.invalid:9093", ["topic"] = "payments.events" }), Secrets);
        var c = k.Config("th-user:th-pass");
        Assert.True(c.EnableIdempotence);
        Assert.Equal(Confluent.Kafka.Acks.All, c.Acks);
        Assert.Equal(Confluent.Kafka.SecurityProtocol.SaslSsl, c.SecurityProtocol);
        Assert.Equal(Confluent.Kafka.SaslMechanism.ScramSha512, c.SaslMechanism);
        Assert.Equal("th-user", c.SaslUsername);
        Assert.Equal("payments.events", k.Topic);
        Assert.Throws<PortBindingException>(() => EventStreamAdapters.Create(Bind(EventStreamAdapters.Port, "kafka", new() { ["bootstrapServers"] = "k:9092", ["topic"] = "t", ["acks"] = "1" }), Secrets));
        Assert.Throws<PortBindingException>(() => EventStreamAdapters.Create(Bind(EventStreamAdapters.Port, "kafka", new() { ["bootstrapServers"] = "k:9092", ["topic"] = "t", ["securityProtocol"] = "none" }), Secrets));
        Assert.Throws<PortBindingException>(() => EventStreamAdapters.Create(Bind(EventStreamAdapters.Port, "kafka", new() { ["topic"] = "t" }), Secrets));
        using var leader = (KafkaEventStream)EventStreamAdapters.Create(Bind(EventStreamAdapters.Port, "kafka", new() { ["bootstrapServers"] = "k:9092", ["topic"] = "t", ["acks"] = "1", ["idempotent"] = "false", ["securityProtocol"] = "plaintext" }), Secrets);
        Assert.Equal(1, leader.Config(null).MaxInFlight);
    }

    [Theory]
    [InlineData("adib"), InlineData("dib"), InlineData("enbd"), InlineData("fab")]
    public void Each_real_bank_binds_both_ports_to_its_production_adapter_by_placeholder_host_and_credential_reference(string bank)
    {
        var p = PortBindings.FromDirectory(PlatformOnlyBankFolderTests.Folder(bank), BankCurrency.Of(PlatformOnlyBankFolderTests.Folder(bank)));
        var at = DateTimeOffset.Parse("2026-11-24T08:00:00Z");
        var n = p.InForce(bank, NotificationAdapters.Port, at);
        var e = p.InForce(bank, EventStreamAdapters.Port, at);
        Assert.Equal(("mqtt", "kafka"), (n.Adapter, e.Adapter));
        Assert.EndsWith(".invalid", n.Settings["host"]);
        Assert.Contains(".invalid:", e.Settings["bootstrapServers"]);
        Assert.Equal($"env:TH_{bank.ToUpperInvariant()}_ALERTS_BROKER_LOGIN", n.Settings["secretRef"]);
        Assert.Equal($"env:TH_{bank.ToUpperInvariant()}_EVENTS_KAFKA_LOGIN", e.Settings["secretRef"]);
        // Each builds without connecting, with the breaker's default dials, and the producer is idempotent with acks all over SASL and TLS.
        var mqtt = (MqttNotification)NotificationAdapters.Create(n, null, null, Secrets);
        Assert.True(mqtt.Tls);
        Assert.Equal($"transacthub/{bank}/alerts/tat.breached", mqtt.TopicOf(Alert(bank)));
        using var kafka = (KafkaEventStream)EventStreamAdapters.Create(e, Secrets);
        var c = kafka.Config(null);
        Assert.True(c.EnableIdempotence);
        Assert.Equal((Confluent.Kafka.Acks.All, Confluent.Kafka.SecurityProtocol.SaslSsl), (c.Acks!.Value, c.SecurityProtocol!.Value));
        Assert.Equal(BreakerDials.Default, BreakerDials.Of(n));
        Assert.Equal(BreakerDials.Default, BreakerDials.Of(e));
    }
}
