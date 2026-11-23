using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using TransactHub.Ports;
using TransactHub.Store;

namespace TransactHub.Operations.Tests;

/// <summary>
/// Payment events out of the store's transactional outbox (R-09-152, R-09-153): every fact is published once its save is
/// committed, as its reference only, in order per payment (the key is the subject), at least once, with the position kept
/// insert-only so a restart resumes and nothing is skipped. On SQL Server and PostgreSQL; on the simulator and on a real Kafka
/// broker in a test container (KRaft, one node, pinned by digest). Synthetic ids only.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class EventStreamTests(Databases dbs, EventStreamTests.Kafka kafka) : IClassFixture<EventStreamTests.Kafka>
{
    const string Bank = "bank-events";

    /// <summary>One Kafka broker for the class: apache/kafka-native 3.9.0 in KRaft mode, three partitions per topic.</summary>
    public sealed class Kafka : IAsyncLifetime
    {
        public const string Image = "apache/kafka-native:3.9.0@sha256:c20b97f0a3990771f52bf7855ccb9ae82ac683a357a101482ba349dfb2ae0cdb";
        readonly int port = FreePort();
        IContainer? container;
        public string Bootstrap => $"localhost:{port}";

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        public async Task InitializeAsync()
        {
            container = new ContainerBuilder(Image)
                .WithPortBinding(port, 9092)
                .WithEnvironment("KAFKA_NODE_ID", "1")
                .WithEnvironment("KAFKA_PROCESS_ROLES", "broker,controller")
                .WithEnvironment("KAFKA_LISTENERS", "PLAINTEXT://:9092,CONTROLLER://:9093")
                .WithEnvironment("KAFKA_ADVERTISED_LISTENERS", $"PLAINTEXT://localhost:{port}")
                .WithEnvironment("KAFKA_CONTROLLER_LISTENER_NAMES", "CONTROLLER")
                .WithEnvironment("KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT")
                .WithEnvironment("KAFKA_CONTROLLER_QUORUM_VOTERS", "1@localhost:9093")
                .WithEnvironment("KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR", "1")
                .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR", "1")
                .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_MIN_ISR", "1")
                .WithEnvironment("KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS", "0")
                .WithEnvironment("KAFKA_NUM_PARTITIONS", "3")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Kafka Server started"))
                .Build();
            await container.StartAsync();
        }

        public async Task DisposeAsync()
        {
            if (container is not null) await container.DisposeAsync();
        }
    }

    static PortBindings Bound(string adapter, string settings = "")
    {
        var b = new PortBindings();
        b.Add($"kind: port-bindings\ntenant: {Bank}\nbindings:\n  - port: p.port.event-stream.v1\n    adapter: {adapter}\n    validFrom: 2026-01-01T00:00:00Z\n{settings}");
        return b;
    }

    /// <summary>Three payments' facts, interleaved, as engines write them: each save is a fact and its outbox row.</summary>
    static async Task<List<(string Subject, long Seq, string Code)>> Write(StoreOptions store, string[] subjects, int each, string run)
    {
        var written = new List<(string, long, string)>();
        await using var db = StoreDbContext.Open(store);
        var log = new FactLog(db);
        for (var i = 0; i < each; i++)
            foreach (var s in subjects)
            {
                var f = await log.Append(s, "payment", new NewFact("step", $"{run}-{i}", new JsonObject { ["step"] = $"s{i}", ["debtorName"] = "Synthetic Trading LLC" }, Actor: "engine"));
                written.Add((s, f.Seq, f.Code));
            }
        return written;
    }

    static async Task<int> Positions(StoreOptions store)
    {
        await using var db = StoreDbContext.Open(store);
        return await db.StreamPositions.AsNoTracking().CountAsync();
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Each_fact_goes_out_once_in_order_as_its_reference_only_and_a_restart_resumes_after_the_last_recorded_position(StoreProvider p)
    {
        var (store, _) = await dbs.Pair(p);
        var subjects = new[] { $"P-{Guid.NewGuid():N}"[..12], $"P-{Guid.NewGuid():N}"[..12], $"P-{Guid.NewGuid():N}"[..12] };
        var first = await Write(store, subjects, 3, "a");
        var before = SimulatedEventStream.Published.Count(e => subjects.Contains(e.Subject));
        using (var relay = new EventStreamRelay(store, Bank, Bound("simulator")) { GapTimeout = TimeSpan.Zero })
        {
            await relay.CatchUp();
            Assert.True(await relay.Position() > 0);
        }
        var got = SimulatedEventStream.Published.Where(e => subjects.Contains(e.Subject)).ToList();
        Assert.Equal(0, before);
        Assert.Equal(first.Select(x => (x.Subject, x.Seq, x.Code)), got.Select(e => (e.Subject, e.Seq, e.Code)));
        Assert.All(got, e => Assert.Equal(("payment", "step", Bank), (e.SubjectKind, e.Kind, e.Tenant)));
        // Only the fact's reference leaves: none of its data.
        Assert.DoesNotContain(got, e => e.ToJson().ToJsonString().Contains("Synthetic Trading"));
        Assert.Equal(got.Select(e => e.Position).Order(), got.Select(e => e.Position));

        // A restart: a new relay resumes after the last recorded position and sends only what is new.
        var second = await Write(store, subjects, 2, "b");
        using (var relay = new EventStreamRelay(store, Bank, Bound("simulator")) { GapTimeout = TimeSpan.Zero }) await relay.CatchUp();
        got = SimulatedEventStream.Published.Where(e => subjects.Contains(e.Subject)).ToList();
        Assert.Equal(first.Concat(second).Select(x => (x.Subject, x.Seq)), got.Select(e => (e.Subject, e.Seq)));

        // A platform that does not answer: nothing is recorded, and once it answers every event goes, none skipped.
        var third = await Write(store, subjects, 1, "c");
        var positions = await Positions(store);
        using (var failing = new EventStreamRelay(store, Bank, Bound("simulator", "    settings:\n      answer: rejected\n")) { GapTimeout = TimeSpan.Zero })
            await Assert.ThrowsAsync<HttpRequestException>(() => failing.CatchUp());
        Assert.Equal(positions, await Positions(store));
        using (var relay = new EventStreamRelay(store, Bank, Bound("simulator")) { GapTimeout = TimeSpan.Zero }) await relay.CatchUp();
        got = SimulatedEventStream.Published.Where(e => subjects.Contains(e.Subject)).ToList();
        Assert.Equal(first.Concat(second).Concat(third).Select(x => (x.Subject, x.Seq)), got.Select(e => (e.Subject, e.Seq)));

        // The position is insert-only: each batch a row of its own, never an update.
        await using var db = StoreDbContext.Open(store);
        var rows = await db.StreamPositions.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(rows.Select(r => r.Through).Order(), rows.Select(r => r.Through));
        var row = rows[0];
        db.Attach(row).Property(x => x.Through).CurrentValue = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task With_no_binding_in_force_the_relay_waits_and_keeps_its_position()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        await Write(store, [$"P-{Guid.NewGuid():N}"[..12]], 2, "n");
        using var relay = new EventStreamRelay(store, Bank, new PortBindings()) { GapTimeout = TimeSpan.Zero };
        Assert.Equal(0, await relay.Poll());
        Assert.Equal(0, await relay.Position());
    }

    [Fact]
    public async Task A_save_still_in_flight_holds_the_stream_back_until_it_is_committed_so_no_event_is_skipped()
    {
        // PostgreSQL: a reader does not wait on another's uncommitted insert, so the relay sees the gap it leaves.
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        var a = $"P-{Guid.NewGuid():N}"[..12];
        var b = $"P-{Guid.NewGuid():N}"[..12];
        await using var slow = StoreDbContext.Open(store);
        await using var tx = await slow.Database.BeginTransactionAsync();
        await new FactLog(slow).Append(a, "payment", new NewFact("step", "slow", new JsonObject(), Actor: "engine"));
        await Write(store, [b], 1, "fast");
        using var relay = new EventStreamRelay(store, Bank, Bound("simulator")) { GapTimeout = TimeSpan.FromMinutes(5) };
        Assert.Equal(0, await relay.Poll());
        Assert.DoesNotContain(SimulatedEventStream.Published, e => e.Subject == b);
        await tx.CommitAsync();
        await relay.CatchUp();
        var got = SimulatedEventStream.Published.Where(e => e.Subject == a || e.Subject == b).ToList();
        Assert.Equal([a, b], got.Select(e => e.Subject));
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task On_a_real_kafka_broker_each_payments_events_arrive_in_order_on_its_key_and_a_restart_sends_nothing_twice_and_skips_nothing(StoreProvider p)
    {
        var (store, _) = await dbs.Pair(p);
        var topic = $"th.events.{Guid.NewGuid():N}"[..24];
        var settings = $"    settings:\n      bootstrapServers: {kafka.Bootstrap}\n      topic: {topic}\n      securityProtocol: plaintext\n      timeoutSeconds: \"20\"\n";
        var subjects = Enumerable.Range(0, 4).Select(_ => $"P-{Guid.NewGuid():N}"[..12]).ToArray();
        var first = await Write(store, subjects, 5, "a");
        using (var relay = new EventStreamRelay(store, Bank, Bound("kafka", settings)) { GapTimeout = TimeSpan.Zero, BatchSize = 7 })
            await relay.CatchUp();
        // The relay stops (a restart) and more facts are written meanwhile; the new relay sends those only.
        var second = await Write(store, subjects, 3, "b");
        using (var relay = new EventStreamRelay(store, Bank, Bound("kafka", settings)) { GapTimeout = TimeSpan.Zero, BatchSize = 7 })
            await relay.CatchUp();

        var all = first.Concat(second).ToList();
        var seen = Consume(topic, all.Count);
        Assert.Equal(all.Count, seen.Count);
        // Nothing twice, nothing skipped, and per payment in the order of its chain, on one partition.
        Assert.Equal(all.Select(x => (x.Subject, x.Seq)).Order(), seen.Select(x => (x.Key, (long)x.Event["seq"]!)).Order());
        foreach (var s in subjects)
        {
            var mine = seen.Where(x => x.Key == s).ToList();
            Assert.Equal(all.Where(x => x.Subject == s).Select(x => x.Seq), mine.Select(x => (long)x.Event["seq"]!));
            Assert.Single(mine.Select(x => x.Partition).Distinct());
            Assert.All(mine, x => Assert.Equal(s, (string?)x.Event["subject"]));
        }
        Assert.DoesNotContain(seen, x => x.Event.ToJsonString().Contains("Synthetic Trading"));
        Assert.True(await Positions(store) >= 2);
    }

    List<(string Key, int Partition, JsonObject Event)> Consume(string topic, int expect)
    {
        using var c = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = kafka.Bootstrap, GroupId = $"th-test-{Guid.NewGuid():N}", AutoOffsetReset = AutoOffsetReset.Earliest, EnableAutoCommit = false,
        }).Build();
        c.Subscribe(topic);
        var got = new List<(string, int, JsonObject)>();
        var until = DateTime.UtcNow.AddSeconds(60);
        // Read until every expected event is in, then a little longer to see that nothing comes twice.
        DateTime? enough = null;
        while (DateTime.UtcNow < until && (enough is null || DateTime.UtcNow < enough))
        {
            var r = c.Consume(TimeSpan.FromMilliseconds(500));
            if (r?.Message is { } m) got.Add((m.Key, r.Partition.Value, (JsonObject)JsonNode.Parse(m.Value)!));
            if (got.Count >= expect && enough is null) enough = DateTime.UtcNow.AddSeconds(3);
        }
        c.Close();
        return got;
    }
}
