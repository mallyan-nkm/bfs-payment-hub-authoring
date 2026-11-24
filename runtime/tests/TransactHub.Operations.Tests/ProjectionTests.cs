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

    /// <summary>
    /// R-09-288 (Issue I-363): the store keeps its copy of the Central Bank SIF under the scheme message's id, as the File API keeps the
    /// sent file. A copy an earlier runtime kept under the binding's old word (<c>uae.wps.sif-out</c>) stays as it is, insert-only, and
    /// the read model takes it as the message: the leg's CB file name is read from it.
    /// </summary>
    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_SIF_kept_under_the_former_kind_is_read_as_the_scheme_message(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store);
        string kept;
        await using (var db = StoreDbContext.Open(store))
            (kept, _) = await new PaymentStore(db).KeepMessage(WpsRun.Tenant, "uae.wps", TransactHub.Model.Direction.Outward, "uae.wps.sif-out", "0000001234567261117083000-CB.SIF", "SCR,synthetic former\n"u8.ToArray(), id);
        await new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using (var db = StoreDbContext.Open(store))
            Assert.Equal("uae.wps.sif-out", (await db.Messages.AsNoTracking().SingleAsync(m => m.Id == kept)).Kind);
        await using var o = OpsDbContext.Open(ops);
        Assert.Equal("uae.wps.sif-submission", (await o.Documents.SingleAsync(d => d.Id == kept)).Kind);
        var leg = await o.Legs.SingleAsync(x => x.SubjectId == id);
        Assert.Equal("0000001234567261117083000-CB.SIF", (string?)JsonNode.Parse(leg.FieldsJson)!["cbFileName"]);
    }

    /// <summary>
    /// R-09-302 (Issue I-402): the read model learns a former kind from the scheme's content, not from platform code. With no scheme
    /// that declares it, a document is read under the kind it was kept under; the published scheme declares the SIF's.
    /// </summary>
    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_former_kind_is_read_as_the_message_only_where_the_scheme_declares_it(StoreProvider p)
    {
        Assert.Equal("uae.wps.sif-submission", Views.FormerKinds["uae.wps.sif-out"]);
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store);
        string kept;
        await using (var db = StoreDbContext.Open(store))
            (kept, _) = await new PaymentStore(db).KeepMessage(WpsRun.Tenant, "uae.wps", TransactHub.Model.Direction.Outward, "uae.wps.sif-out", "0000001234567261117083000-CB.SIF", "SCR,synthetic former\n"u8.ToArray(), id);
        await new Projector(store, ops, new ViewCatalog(Views.All)) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using var o = OpsDbContext.Open(ops);
        Assert.Equal("uae.wps.sif-out", (await o.Documents.SingleAsync(d => d.Id == kept)).Kind);
    }

    /// <summary>R-09-302: the former kinds are read from every release of a scheme manifest in the folder, and one kind names one message.</summary>
    [Fact]
    public void The_catalogue_reads_former_kinds_from_the_scheme_manifests_and_refuses_one_kind_for_two_messages()
    {
        var dir = Directory.CreateTempSubdirectory("th-former-kinds-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "1.0.0.yaml"), "kind: scheme\nscope: x.demo\nrelease: 1.0.0\nmessages:\n  - id: x.demo.file-out\n    direction: out\n");
            File.WriteAllText(Path.Combine(dir, "1.1.0.yaml"), "kind: scheme\nscope: x.demo\nrelease: 1.1.0\nmessages:\n  - id: x.demo.file-submission\n    direction: out\n    formerKinds: [x.demo.file-out, x.demo.old]\n  - id: x.demo.reply\n    direction: in\n");
            var c = ViewCatalog.FromFolder(dir);
            Assert.Equal("x.demo.file-submission", c.CurrentKind("x.demo.file-out"));
            Assert.Equal("x.demo.file-submission", c.CurrentKind("x.demo.old"));
            Assert.Equal("x.demo.reply", c.CurrentKind("x.demo.reply"));
            Assert.Equal(2, c.FormerKinds.Count);
            File.WriteAllText(Path.Combine(dir, "1.2.0.yaml"), "kind: scheme\nscope: x.demo\nrelease: 1.2.0\nmessages:\n  - id: x.demo.reply\n    direction: in\n    formerKinds: [x.demo.old]\n");
            var e = Assert.Throws<InvalidOperationException>(() => ViewCatalog.FromFolder(dir));
            Assert.Contains("x.demo.old", e.Message);
        }
        finally { Directory.Delete(dir, true); }
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
        // R-05-72: the cut-off the file met, as the step applied it.
        Assert.Equal("taken", (string?)lf["cutOff"]);
        Assert.Equal("true", lf["cutOffApplied"]?.ToString());
        Assert.Equal("14:00", (string?)lf["cutOffTime"]);
        Assert.Equal("Asia/Dubai", (string?)lf["cutOffZone"]);
        Assert.Equal("2026-11-17T15:00:00", (string?)lf["acceptedLocal"]);
        Assert.Equal("2026-11-18", (string?)lf["countsFor"]);
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

    /// <summary>
    /// R-05-103, R-09-236 (Issue I-337): a payment whose file the File API took but never delivered says so. The leg's sent time is the
    /// delivery's, not the outbox's acceptance; the delivery, its attempts and its error are the leg's fields; the facts are on the
    /// payment's timeline; the payment is listed among those whose file is undelivered until a delivery puts it on the partner's folder.
    /// </summary>
    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_file_the_File_API_never_delivered_shows_on_the_payment_its_leg_and_the_undelivered_list(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store, delivery: "undelivered");
        var projector = new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero };
        await projector.CatchUp();
        JsonNode LegFields()
        {
            using var o = OpsDbContext.Open(ops);
            return JsonNode.Parse(o.Legs.Single(x => x.SubjectId == id).FieldsJson)!;
        }
        var lf = LegFields();
        Assert.Null(lf["sentToCbAt"]);
        Assert.NotNull((string?)lf["outboxTookAt"]);
        Assert.Equal(("undelivered", "port.CircuitOpen", 1), ((string?)lf["delivery"], (string?)lf["deliveryError"], (int?)lf["deliveryAttempts"]));
        await using (var o = OpsDbContext.Open(ops))
            Assert.Equal(["file.delivery-failed", "file.undelivered"], await o.Timeline.AsNoTracking().Where(x => x.SubjectId == id && x.Kind.StartsWith("file.")).OrderBy(x => x.Seq).Select(x => x.Kind).ToListAsync());
        var list = new UndeliveredFiles(ops);
        var row = Assert.Single(await list.List([WpsRun.Tenant]));
        Assert.Equal((id, UndeliveredFiles.Undelivered, "uae.wps.sif-submission", "port.CircuitOpen", 1), (row.Payment, row.State, row.Kind, row.Code, row.Attempts));
        Assert.Empty(await list.List(["another-bank"]));
        // The partner answers and a resend delivers the kept file: the leg's sent time is the delivery's, and the list is empty.
        await using (var db = StoreDbContext.Open(store))
            await new FactLog(db).Append(id, "bulk", new NewFact("file.delivered", "sftp", new JsonObject
            {
                ["file"] = row.File, ["kind"] = "uae.wps.sif-submission", ["fileName"] = row.FileName, ["adapter"] = "sftp", ["attempt"] = 2, ["resend"] = true,
            }, Actor: "operator-1"));
        await projector.CatchUp();
        lf = LegFields();
        Assert.Equal(("delivered", 2), ((string?)lf["delivery"], (int?)lf["deliveryAttempts"]));
        Assert.NotNull((string?)lf["sentToCbAt"]);
        Assert.Null(lf["deliveryError"]);
        Assert.Empty(await list.List([WpsRun.Tenant]));
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_initiator_the_posting_s_charges_and_the_duration_reach_the_console_and_the_initiator_masked(StoreProvider p)
    {
        // Issue I-100: GrpHdr/InitgPty by id only, masked; camt.054 ChrgsInf and the total debited; the duration from payment.end (I-97).
        var (store, ops) = await dbs.Pair(p);
        var id = await WpsRun.Seed(store, hold: false, console: true);
        await new Projector(store, ops, Views) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using var o = OpsDbContext.Open(ops);
        var f = JsonNode.Parse((await o.Subjects.SingleAsync(x => x.Id == id)).FieldsJson)!;
        Assert.NotNull((string?)f["initiatedBy"]);
        Assert.NotEqual("U-000000123456", (string?)f["initiatedBy"]);
        Assert.Equal("1510.50", (string?)f["postingTotal"]);
        Assert.Equal("10.00", (string?)f["chargeAmount"]);
        Assert.Equal("0.50", (string?)f["vatAmount"]);
        Assert.Equal("settled", (string?)f["endOutcome"]);
        Assert.StartsWith("P", (string?)f["duration"]);
        Assert.DoesNotContain("U-000000123456", await Snapshot(ops));
    }

    internal static OperatorIdentity Operator(string sub, string tenant, params string[] roles) =>
        new(sub, sub, new Dictionary<string, IReadOnlySet<string>> { [tenant] = new HashSet<string>(roles.Length == 0 ? [OperatorRoles.Operator] : roles) });
}
