using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;
using TransactHub.Ports;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Operations.Tests;

/// <summary>
/// The operations console's turnaround alerts through the platform's notification port (R-05-80, R-09-150, R-09-151), on a real
/// MQTT broker in a test container (eclipse-mosquitto, pinned by digest, a login of its own); and the turnaround limit counted in
/// business days of the bank's calendar, its closing days included (R-05-81). On SQL Server and PostgreSQL. Synthetic data only.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class NotificationTests(Databases dbs, NotificationTests.Broker broker) : IClassFixture<NotificationTests.Broker>
{
    const string Bank = WpsRun.Tenant;
    const string User = "th-alerts", Password = "synthetic-broker-pass";

    /// <summary>One Mosquitto broker for the class, anonymous logins refused.</summary>
    public sealed class Broker : IAsyncLifetime
    {
        public const string Image = "eclipse-mosquitto:2.0.20@sha256:21421af7b32bf9ce508e9090c8eb13bb81f410ca778dc205506180a6f862d0eb";
        IContainer? container;
        public string Host => container!.Hostname;
        public int Port => container!.GetMappedPublicPort(1883);

        public async Task InitializeAsync()
        {
            container = new ContainerBuilder(Image)
                .WithPortBinding(1883, true)
                .WithEntrypoint("sh", "-c")
                .WithCommand($"printf 'listener 1883\\nallow_anonymous false\\npassword_file /tmp/pw\\n' > /tmp/m.conf && mosquitto_passwd -b -c /tmp/pw {User} {Password} && chown mosquitto:mosquitto /tmp/pw && chmod 0700 /tmp/pw && exec mosquitto -c /tmp/m.conf")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("running"))
                .Build();
            await container.StartAsync();
        }

        public async Task DisposeAsync()
        {
            if (container is not null) await container.DisposeAsync();
        }
    }

    static readonly ISecretResolver Secrets = SecretResolver.FromEnvironment(k => k switch
    {
        "TH_TEST_BROKER_LOGIN" => $"{User}:{Password}",
        "TH_TEST_BROKER_WRONG" => $"{User}:not-the-password",
        _ => null,
    });

    PortBindings Mqtt(string login, string tenant = Bank)
    {
        var b = new PortBindings();
        b.Add($"kind: port-bindings\ntenant: {tenant}\nbindings:\n  - port: p.port.notification.v1\n    adapter: mqtt\n    validFrom: 2026-01-01T00:00:00Z\n    settings:\n" +
              $"      host: {broker.Host}\n      port: \"{broker.Port}\"\n      tls: \"false\"\n      qos: \"1\"\n      topic: th-test/{{tenant}}/{{kind}}\n      secretRef: env:{login}\n      breakerFailures: \"2\"\n      timeoutSeconds: \"5\"\n");
        return b;
    }

    static async Task Dial(StoreOptions store, string parameter, string value, DateTimeOffset from)
    {
        await using var db = StoreDbContext.Open(store);
        await new VersionedValues(db).Publish(OperationsDials.ConfigKind, Bank, parameter, new JsonObject { ["value"] = value, ["configurationSet"] = "1.15.0" }, from, null, "test");
    }

    static async Task<DateTimeOffset> AcceptedAt(StoreOptions ops, string id)
    {
        await using var o = OpsDbContext.Open(ops);
        return (await o.Subjects.AsNoTracking().SingleAsync(s => s.Id == id)).AcceptedAt;
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_turnaround_breach_is_alerted_once_on_the_banks_mqtt_broker_with_ids_only_and_a_broker_that_refuses_is_recorded(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store, hold: false);
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        var accepted = await AcceptedAt(ops, id);
        await Dial(store, "uae.wps.view.operations.v1#tatLimit", "PT1S", accepted.AddDays(-1));

        // An operator's alerting tool, subscribed to the bank's alert topics.
        var factory = new MqttClientFactory();
        using var sub = factory.CreateMqttClient();
        var got = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        sub.ApplicationMessageReceivedAsync += e => { if (e.ApplicationMessage.Topic == $"th-test/{Bank}/tat.breached") got.TrySetResult(e.ApplicationMessage); return Task.CompletedTask; };
        await sub.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(broker.Host, broker.Port).WithCredentials(User, Password).WithClientId($"th-test-{Guid.NewGuid():N}").Build());
        await sub.SubscribeAsync(factory.CreateSubscribeOptionsBuilder().WithTopicFilter($"th-test/{Bank}/#", MqttQualityOfServiceLevel.AtLeastOnce).Build());

        var clock = new ManualClock(accepted.AddMinutes(5));
        var record = new ConsoleRecord(store, clock);
        var tat = new Turnaround(store, ops, new OperationsDials(store, Repo.Views()), record, new OperatorAlerts(Mqtt("TH_TEST_BROKER_LOGIN"), _ => { }, secrets: Secrets), clock);
        Assert.Equal(1, await tat.Sweep());
        var m = await got.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, m.QualityOfServiceLevel);
        var body = Encoding.UTF8.GetString(m.Payload.ToArray());
        var alert = JsonNode.Parse(body)!;
        Assert.Equal("tat.breached", (string?)alert["kind"]);
        Assert.Equal(id, (string?)alert["subject"]);
        Assert.Equal("PT1S", (string?)alert["data"]!["limit"]);
        // Ids only: no account, no name, no amount of the file.
        Assert.DoesNotContain("AE07", body);
        Assert.DoesNotContain("Example Trading", body);
        Assert.Equal(0, await tat.Sweep());
        var facts = await record.All(ConsoleRecord.TatKind);
        var breached = Assert.Single(facts, f => f.Code == "breached");
        Assert.Equal("mqtt", (string?)JsonNode.Parse(breached.DataJson)!["adapter"]);
        Assert.DoesNotContain(facts, f => f.Code == "alert-failed");

        // A broker that refuses the login: the breach is still recorded, its alert recorded as failed, and not retried.
        var (store2, ops2) = await dbs.Pair(p);
        var id2 = await WpsRun.Seed(store2, hold: false);
        await new Projector(store2, ops2, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await Dial(store2, "uae.wps.view.operations.v1#tatLimit", "PT1S", accepted.AddDays(-1));
        var clock2 = new ManualClock((await AcceptedAt(ops2, id2)).AddMinutes(5));
        var record2 = new ConsoleRecord(store2, clock2);
        var refused = new Turnaround(store2, ops2, new OperationsDials(store2, Repo.Views()), record2, new OperatorAlerts(Mqtt("TH_TEST_BROKER_WRONG", Bank), _ => { }, secrets: Secrets), clock2);
        Assert.Equal(1, await refused.Sweep());
        var failed = Assert.Single(await record2.All(ConsoleRecord.TatKind), f => f.Code == "alert-failed");
        Assert.Equal("IOException", (string?)JsonNode.Parse(failed.DataJson)!["why"]);
        Assert.Equal(0, await refused.Sweep());
    }
}

/// <summary>The turnaround limit counted in business days of the bank's calendar, its closing days included (R-05-81).</summary>
[Collection(DatabaseCollection.Name)]
public sealed class TurnaroundCalendarTests(Databases dbs)
{
    const string Bank = WpsRun.Tenant;

    static async Task Dial(StoreOptions store, string parameter, string value, DateTimeOffset from)
    {
        await using var db = StoreDbContext.Open(store);
        await new VersionedValues(db).Publish(OperationsDials.ConfigKind, Bank, parameter, new JsonObject { ["value"] = value, ["configurationSet"] = "1.15.0" }, from, null, "test");
    }

    static async Task<DateTimeOffset> AcceptedAt(StoreOptions ops, string id)
    {
        await using var o = OpsDbContext.Open(ops);
        return (await o.Subjects.AsNoTracking().SingleAsync(s => s.Id == id)).AcceptedAt;
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_turnaround_limit_counts_business_days_of_the_banks_calendar_so_its_closing_day_does_not_count(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store, hold: false);
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        var accepted = await AcceptedAt(ops, id);
        var day = accepted.UtcDateTime.Date;
        string D(DateTime d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        // A business calendar with no weekend (one holiday far away), and the bank's own closing day the day after acceptance.
        await using (var db = StoreDbContext.Open(store))
        {
            var values = new VersionedValues(db);
            await values.Publish("reference", Bank, "business-calendar", new JsonObject
            {
                ["table"] = new JsonObject { ["name"] = "uae.p.ref.business-calendar.v1", ["editions"] = new JsonArray(new JsonObject { ["validFrom"] = "2020-01-01", ["rows"] = new JsonArray(new JsonObject { ["day"] = "2099-01-01" }) }) },
                ["configurationRelease"] = "test",
            }, DateTimeOffset.Parse("2020-01-01T00:00:00Z"), null, "test");
            await values.Publish("reference", Bank, "bank-calendar", new JsonObject
            {
                ["table"] = new JsonObject { ["name"] = "b.ref.bank-calendar.v1", ["editions"] = new JsonArray(new JsonObject { ["validFrom"] = "2020-01-01", ["rows"] = new JsonArray(new JsonObject { ["day"] = D(day.AddDays(1)), ["kind"] = "closed" }) }) },
                ["configurationRelease"] = "test",
            }, DateTimeOffset.Parse("2020-01-01T00:00:00Z"), null, "test");
        }
        await Dial(store, "p.view.operations.v1#tatLimit", "P1D", accepted.AddDays(-1));
        var clock = new ManualClock(accepted);
        var tat = new Turnaround(store, ops, new OperationsDials(store, Repo.Views()), new ConsoleRecord(store, clock), new OperatorAlerts(null, _ => { }), clock);

        // A day and a half later, over the closing day: in elapsed time a breach, in business days not yet.
        clock.Set(accepted.AddHours(36));
        Assert.Empty(await tat.Breaches([Bank]));
        // The limit ends a business day after acceptance: the day after the closing day, at the same time.
        clock.Set(accepted.AddDays(2).AddSeconds(-1));
        Assert.Empty(await tat.Breaches([Bank]));
        clock.Set(accepted.AddDays(2).AddSeconds(1));
        var b = Assert.Single(await tat.Breaches([Bank]));
        Assert.Equal(id, (string?)b["id"]);
        Assert.Equal("business", (string?)b["calendar"]);
        Assert.Equal("uae.p.ref.business-calendar.v1+b.ref.bank-calendar.v1", (string?)b["calendarTable"]);
        Assert.Equal(accepted.AddDays(2), DateTimeOffset.Parse((string)b["deadline"]!));
        Assert.Equal("PT1S", (string?)b["over"]);
        Assert.Equal("P2DT1S", (string?)b["elapsed"]);
    }
}
