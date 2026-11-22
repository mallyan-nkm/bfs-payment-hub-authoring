using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations.Tests;

/// <summary>
/// The operations read model is a projection of the store's facts through the outbox (R-05-49), on SQL Server and
/// PostgreSQL alike: following the outbox and rebuilding from the facts give the same model; replaying changes
/// nothing; a pass in small batches resumes from its checkpoint; the lag is measured; personal values are masked.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ProjectionTests(Databases dbs)
{
    static readonly ViewCatalog Views = Repo.Views();

    /// <summary>Everything the read model holds, with its own row numbers and the checkpoint's clock left out.</summary>
    static async Task<string> Snapshot(StoreOptions ops)
    {
        await using var o = OpsDbContext.Open(ops);
        var model = new
        {
            subjects = await o.Subjects.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            legs = await o.Legs.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            lines = await o.Lines.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            holds = await o.Holds.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            actions = await o.Actions.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            documents = await o.Documents.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            streams = await o.Streams.AsNoTracking().OrderBy(x => x.StreamId).ToListAsync(),
            timeline = (await o.Timeline.AsNoTracking().ToListAsync()).OrderBy(x => x.StreamId).ThenBy(x => x.Seq).Select(x => new { x.SubjectId, x.StreamId, x.Seq, x.At, x.Kind, x.Code, x.Instance, x.Step, x.Activity, x.Actor, x.DataJson }),
            values = (await o.FieldValues.AsNoTracking().ToListAsync()).OrderBy(x => x.SubjectId).ThenBy(x => x.Field).Select(x => new { x.SubjectId, x.Field, x.Text, x.Number }),
        };
        return JsonSerializer.Serialize(model);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Following_the_outbox_and_rebuilding_from_the_facts_give_the_same_read_model(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var first = await WpsRun.Seed(store);
        var projector = new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero };
        await projector.CatchUp();
        // More facts after the first pass: an operator decides the hold, a second file arrives.
        var actions = new OperatorActions(store, ops, Views, new OutboxSignals(store));
        await actions.Request(Operator("op-1", "example-bank"), first, "approve", "within the employer's agreed limit");
        await WpsRun.Seed(store, fileId: "0000001234567261117083111", hold: false, ack: "nak");
        await projector.CatchUp();
        var incremental = await Snapshot(ops);

        await projector.Rebuild();
        var rebuilt = await Snapshot(ops);
        Assert.Equal(incremental, rebuilt);
        Assert.Contains("approved", incremental);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Replaying_the_outbox_changes_nothing_and_a_pass_in_small_batches_resumes_from_its_checkpoint(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        await WpsRun.Seed(store);
        var whole = new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero };
        await whole.CatchUp();
        var once = await Snapshot(ops);

        // Replay every outbox row again: each fact is applied by its number on its chain, so nothing changes.
        await using (var o = OpsDbContext.Open(ops))
        {
            var cp = await o.Checkpoints.FirstAsync();
            cp.Through = 0;
            await o.SaveChangesAsync();
        }
        await whole.CatchUp();
        Assert.Equal(once, await Snapshot(ops));

        // From nothing, three outbox rows a pass, a new projector each pass (a restart): the same model as a rebuild.
        var (store2, ops2) = await dbs.Pair(p);
        await WpsRun.Seed(store2);
        var passes = 0;
        while (await new Projector(store2, ops2, Views) { GapTimeout = TimeSpan.Zero, BatchSize = 3 }.Poll() > 0) passes++;
        Assert.True(passes > 5, $"only {passes} passes");
        var resumed = await Snapshot(ops2);
        await new Projector(store2, ops2, Views) { GapTimeout = TimeSpan.Zero }.Rebuild();
        Assert.Equal(resumed, await Snapshot(ops2));
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_lag_is_measured_and_shown(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        await WpsRun.Seed(store);
        var projector = new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero };
        var before = await projector.Lag();
        Assert.True(before.Behind > 10, $"behind {before.Behind}");
        Assert.True(before.OldestSeconds >= 0);
        await projector.CatchUp();
        var after = await projector.Lag();
        Assert.Equal(0, after.Behind);
        Assert.True(after.Applied >= before.Behind - 1);
        Assert.NotNull(after.LastFactAt);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_WPS_file_shows_its_declared_fields_its_leg_and_its_lines_masked_as_declared(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store);
        await new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using var o = OpsDbContext.Open(ops);
        var s = await o.Subjects.SingleAsync(x => x.Id == id);
        var f = JsonNode.Parse(s.FieldsJson)!;
        Assert.Equal("open", s.Hold);
        Assert.Contains("AM02", s.HoldReason);
        Assert.Equal("ACCP", s.Status);
        Assert.Equal("0000001234567261117083000.SIF", (string?)f["fileName"]);
        Assert.Equal("0000*****4567", (string?)f["employerId"]);
        Assert.Equal("AE07***************3999", (string?)f["debitAccount"]);
        Assert.Equal("Example Trading LLC", (string?)f["corporateName"]);
        Assert.Equal(1, (int?)f["systemAccepted"]);
        Assert.Equal(1, (int?)f["systemRejected"]);
        Assert.Equal(1, (int?)f["ownBankAccounts"]);
        Assert.Equal(1, (int?)f["otherBankAccounts"]);
        Assert.Equal("clear", (string?)f["sanctions"]);
        Assert.Equal("uae.wps", (string?)f["rail"]);
        var leg = await o.Legs.SingleAsync(x => x.SubjectId == id);
        Assert.Equal("uae.wps.wf.salary-file-out.v1", leg.Leg);
        var lf = JsonNode.Parse(leg.FieldsJson)!;
        Assert.Equal("ack", (string?)lf["cbResponse"]);
        Assert.Equal("ACCP", (string?)lf["ackStatus"]);
        Assert.NotNull((string?)lf["sentToCbAt"]);
        var lines = await o.Lines.Where(x => x.BulkId == id).OrderBy(x => x.LineNo).ToListAsync();
        var l1 = JsonNode.Parse(lines[0].FieldsJson)!;
        Assert.Equal("AE07***************3456", (string?)l1["creditAccount"]);
        Assert.Equal("0000******7890", (string?)l1["employeeId"]);
        Assert.Equal("pass", (string?)l1["lineStatus"]);
        Assert.Equal("repair", (string?)JsonNode.Parse(lines[1].FieldsJson)!["lineStatus"]);
        // No full account number or person id is anywhere in the read model.
        var all = await Snapshot(ops);
        foreach (var secret in new[] { "AE070331234567890123456", "AE970336543210987654321", "00001234567890", "00001234567891", "AE070331234567890123999" })
            Assert.DoesNotContain(secret, all);
        // The filters search the declared fields.
        Assert.True(await o.FieldValues.AnyAsync(v => v.SubjectId == id && v.Field == "uae.wps.view.operations.v1:uae.wps.wf.salary-file-out.v1:cbResponse" && v.Text == "ack"));
        Assert.True(await o.FieldValues.AnyAsync(v => v.SubjectId == id && v.Field == "uae.wps.view.operations.v1:subject:totalAmount" && v.Number == 1500m));
    }

    internal static OperatorIdentity Operator(string sub, string tenant, params string[] roles) =>
        new(sub, sub, new Dictionary<string, IReadOnlySet<string>> { [tenant] = new HashSet<string>(roles.Length == 0 ? [OperatorRoles.Operator] : roles) });
}
