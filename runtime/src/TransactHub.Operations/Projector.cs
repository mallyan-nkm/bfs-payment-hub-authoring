using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>
/// The projection of the store's facts into the operations read model (R-05-49). It follows the outbox in order, from a
/// checkpoint, and applies each fact once: a fact stream (one subject's chain) is applied by number, so a fact seen
/// twice is skipped and a fact seen out of order first brings in the ones before it from the store. An outbox number
/// that is missing (a write still in flight) holds the checkpoint back until it arrives, or until a later row is older
/// than <see cref="GapTimeout"/>, when it is taken for a write that was rolled back. <see cref="Rebuild"/> throws the
/// read model away and replays every fact; the result is the same as following the outbox (the test shows it).
/// </summary>
public sealed class Projector(StoreOptions store, StoreOptions ops, ViewCatalog views, TimeProvider? clock = null)
{
    public const string Name = "operations";
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public TimeSpan GapTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int BatchSize { get; init; } = 500;
    static readonly Regex ItemFacts = new(@"^items\[(\d+)\]\.facts(\.|$)");

    public async Task Migrate(CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        await o.Database.MigrateAsync(ct);
    }

    // One projection at a time in a process (the console's API polls and catches up after an action). An installation runs
    // one projecting process; a second one would only redo work, since applying is by number.
    readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>One pass over the outbox: applies what is new and returns how many outbox rows it read.</summary>
    public async Task<int> Poll(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await PollOnce(ct); }
        finally { gate.Release(); }
    }

    async Task<int> PollOnce(CancellationToken ct)
    {
        await using var s = StoreDbContext.Open(store);
        await using var o = OpsDbContext.Open(ops);
        var cp = await Checkpoint(o, ct);
        var rows = await s.Outbox.AsNoTracking().Where(x => x.Id > cp.Through).OrderBy(x => x.Id).Take(BatchSize).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        var run = new Run(s, o, views, clock);
        var expected = cp.Through + 1;
        var holding = false;
        foreach (var r in rows)
        {
            if (r.Id != expected && !holding)
            {
                // A gap: a write still in flight, or one that was rolled back. Rows after it are applied (by number, so
                // nothing is applied twice), but the checkpoint waits for the gap unless it is older than the timeout.
                if (clock.GetUtcNow() - r.At > GapTimeout) expected = r.Id;
                else holding = true;
            }
            await run.Apply(r, ct);
            if (!holding && r.Id == expected) expected = r.Id + 1;
        }
        await run.Finish(ct);
        cp.Through = expected - 1;
        cp.Applied += run.Applied;
        if (run.LastFactAt is { } at) cp.LastFactAt = at;
        if (run.Applied > 0) cp.ProjectedAt = clock.GetUtcNow();
        await o.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>Polls until the outbox is drained.</summary>
    public async Task CatchUp(CancellationToken ct = default)
    {
        while (await Poll(ct) > 0) { }
    }

    /// <summary>Throws the read model away and replays every fact and message in the store.</summary>
    public async Task Rebuild(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { await RebuildOnce(ct); }
        finally { gate.Release(); }
        await CatchUp(ct);
    }

    async Task RebuildOnce(CancellationToken ct)
    {
        var startedAt = clock.GetUtcNow();
        await using var s = StoreDbContext.Open(store);
        await using (var o = OpsDbContext.Open(ops))
        {
            await o.Actions.ExecuteDeleteAsync(ct);
            await o.Holds.ExecuteDeleteAsync(ct);
            await o.Lines.ExecuteDeleteAsync(ct);
            await o.Documents.ExecuteDeleteAsync(ct);
            await o.Timeline.ExecuteDeleteAsync(ct);
            await o.FieldValues.ExecuteDeleteAsync(ct);
            await o.Legs.ExecuteDeleteAsync(ct);
            await o.Subjects.ExecuteDeleteAsync(ct);
            await o.Streams.ExecuteDeleteAsync(ct);
            await o.Checkpoints.ExecuteDeleteAsync(ct);
        }
        // Where following the outbox will start again: rows written well before the rebuild began are in what it replays.
        var settled = startedAt - GapTimeout;
        var through = await s.Outbox.AsNoTracking().Where(x => x.At <= settled).OrderByDescending(x => x.Id).Select(x => (long?)x.Id).FirstOrDefaultAsync(ct) ?? 0;
        long last = 0;
        long applied = 0;
        DateTimeOffset? lastAt = null;
        while (true)
        {
            var facts = await s.Facts.AsNoTracking().Where(f => f.Id > last).OrderBy(f => f.Id).Take(BatchSize).ToListAsync(ct);
            if (facts.Count == 0) break;
            await using var o = OpsDbContext.Open(ops);
            var run = new Run(s, o, views, clock);
            foreach (var f in facts) await run.ApplyFact(f, ct);
            await run.Finish(ct);
            await o.SaveChangesAsync(ct);
            applied += run.Applied;
            lastAt = run.LastFactAt ?? lastAt;
            last = facts[^1].Id;
        }
        var lastMessage = "";
        while (true)
        {
            var ms = await s.Messages.AsNoTracking().Where(m => string.Compare(m.Id, lastMessage) > 0).OrderBy(m => m.Id)
                .Select(m => new { m.Id, m.SubjectId, m.Kind, m.At }).Take(BatchSize).ToListAsync(ct);
            if (ms.Count == 0) break;
            await using var o = OpsDbContext.Open(ops);
            var run = new Run(s, o, views, clock);
            foreach (var m in ms) await run.ApplyMessage(m.Id, ct);
            await run.Finish(ct);
            await o.SaveChangesAsync(ct);
            lastMessage = ms[^1].Id;
        }
        var lastFile = "";
        while (true)
        {
            var fs = await s.StoredFiles.AsNoTracking().Where(f => string.Compare(f.Id, lastFile) > 0).OrderBy(f => f.Id).Select(f => f.Id).Take(BatchSize).ToListAsync(ct);
            if (fs.Count == 0) break;
            await using var o = OpsDbContext.Open(ops);
            var run = new Run(s, o, views, clock);
            foreach (var f in fs) await run.ApplyFile(f, ct);
            await run.Finish(ct);
            await o.SaveChangesAsync(ct);
            lastFile = fs[^1];
        }
        await using (var o = OpsDbContext.Open(ops))
        {
            var cp = await Checkpoint(o, ct);
            cp.Through = through;
            cp.Applied = applied;
            cp.LastFactAt = lastAt;
            cp.RebuiltAt = clock.GetUtcNow();
            cp.ProjectedAt = clock.GetUtcNow();
            await o.SaveChangesAsync(ct);
        }
    }

    static async Task<OpsCheckpoint> Checkpoint(OpsDbContext o, CancellationToken ct)
    {
        var cp = await o.Checkpoints.FirstOrDefaultAsync(x => x.Name == Name, ct);
        if (cp is null) { cp = new OpsCheckpoint { Name = Name }; o.Checkpoints.Add(cp); }
        return cp;
    }

    /// <summary>How far behind the read model is: outbox rows not yet through the checkpoint, and the age of the oldest.</summary>
    public async Task<Lag> Lag(CancellationToken ct = default)
    {
        await using var s = StoreDbContext.Open(store);
        await using var o = OpsDbContext.Open(ops);
        var cp = await o.Checkpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Name == Name, ct) ?? new OpsCheckpoint { Name = Name };
        var behind = await s.Outbox.AsNoTracking().CountAsync(x => x.Id > cp.Through, ct);
        var oldest = await s.Outbox.AsNoTracking().Where(x => x.Id > cp.Through).OrderBy(x => x.Id).Select(x => (DateTimeOffset?)x.At).FirstOrDefaultAsync(ct);
        var now = clock.GetUtcNow();
        return new Lag(behind, oldest is { } t ? Math.Max(0, (now - t).TotalSeconds) : 0, cp.Through, cp.Applied, cp.LastFactAt, cp.ProjectedAt, cp.RebuiltAt);
    }

    /// <summary>One batch of applying: the facts, then each touched payment's derived fields.</summary>
    sealed class Run(StoreDbContext s, OpsDbContext o, ViewCatalog views, TimeProvider clock)
    {
        readonly Dictionary<string, OpsStream> streams = new();
        readonly HashSet<string> touched = new();
        public long Applied;
        public DateTimeOffset? LastFactAt;

        async Task<OpsStream> Stream(string id, CancellationToken ct)
        {
            if (streams.TryGetValue(id, out var st)) return st;
            st = await o.Streams.FirstOrDefaultAsync(x => x.StreamId == id, ct) ?? o.Streams.Add(new OpsStream { StreamId = id }).Entity;
            return streams[id] = st;
        }

        public async Task Apply(OutboxRow r, CancellationToken ct)
        {
            var p = JsonNode.Parse(r.PayloadJson) as JsonObject ?? new JsonObject();
            if (r.Topic == Outbox.MessageTopic) { await ApplyMessage((string)p["message"]!, ct); return; }
            if (r.Topic == FileStore.FileTopic) { await ApplyFile((string)p["file"]!, ct); return; }
            if (r.Topic != Outbox.FactTopic) return;
            var subject = (string)p["subject"]!;
            var seq = (int)p["seq"]!;
            var st = await Stream(subject, ct);
            if (seq <= st.LastSeq) return;
            // Facts of the stream not yet applied, up to this one, in their order: a gap fills itself from the store.
            var facts = await s.Facts.AsNoTracking().Where(f => f.SubjectId == subject && f.Seq > st.LastSeq && f.Seq <= seq).OrderBy(f => f.Seq).ToListAsync(ct);
            foreach (var f in facts) await ApplyFact(f, ct);
        }

        public async Task ApplyFact(FactRow f, CancellationToken ct)
        {
            var st = await Stream(f.SubjectId, ct);
            if (f.Seq <= st.LastSeq) return;
            if (f.Seq != st.LastSeq + 1)
            {
                // Out of order (a rebuild reads by id, a racing writer can commit later): bring in the ones before first.
                var before = await s.Facts.AsNoTracking().Where(x => x.SubjectId == f.SubjectId && x.Seq > st.LastSeq && x.Seq < f.Seq).OrderBy(x => x.Seq).ToListAsync(ct);
                foreach (var b in before) await ApplyOne(st, b, ct);
            }
            await ApplyOne(st, f, ct);
        }

        async Task ApplyOne(OpsStream st, FactRow f, CancellationToken ct)
        {
            if (f.Seq <= st.LastSeq) return;
            st.LastSeq = f.Seq;
            Applied++;
            LastFactAt = LastFactAt is { } l && l > f.At ? l : f.At;
            var data = JsonNode.Parse(f.DataJson) as JsonObject ?? new JsonObject();
            if (f.SubjectKind is "bulk" or "payment") await ApplySubjectFact(f, data, ct);
            else if (f.Kind == "payload") await ApplyPayloadFact(st, f, data, ct);
        }

        async Task<OpsSubject?> Subject(string id, string kind, CancellationToken ct)
        {
            var subj = o.Subjects.Local.FirstOrDefault(x => x.Id == id) ?? await o.Subjects.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (subj is not null) return subj;
            if (kind == "bulk")
            {
                var b = await s.Bulks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                if (b is null) return null;
                subj = new OpsSubject
                {
                    Id = b.Id, Kind = "bulk", Tenant = b.Tenant, Scheme = b.Scheme, Direction = b.Direction.ToLowerInvariant(), ReleaseId = b.ReleaseId,
                    MessageId = b.MessageId, Amount = b.ControlSum, Count = b.NumberOfTransactions, AcceptedAt = b.AcceptedAt, UpdatedAt = b.AcceptedAt,
                };
                var lines = await s.Payments.AsNoTracking().Where(x => x.BulkId == id).OrderBy(x => x.LineNo).ToListAsync(ct);
                subj.Currency = lines.FirstOrDefault()?.Currency ?? "";
                foreach (var l in lines) o.Lines.Add(new OpsLine { Id = l.Id, BulkId = id, LineNo = l.LineNo, Amount = l.Amount, Currency = l.Currency });
            }
            else
            {
                var p = await s.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                if (p is null) return null;
                subj = new OpsSubject
                {
                    Id = p.Id, Kind = "payment", Tenant = p.Tenant, Scheme = p.Scheme, Direction = p.Direction.ToLowerInvariant(), ReleaseId = p.ReleaseId, BulkId = p.BulkId,
                    EndToEndId = p.EndToEndId, Amount = p.Amount, Currency = p.Currency, Count = 1, AcceptedAt = p.AcceptedAt, UpdatedAt = p.AcceptedAt,
                };
            }
            o.Subjects.Add(subj);
            return subj;
        }

        async Task ApplySubjectFact(FactRow f, JsonObject data, CancellationToken ct)
        {
            var subj = await Subject(f.SubjectId, f.SubjectKind, ct);
            if (subj is null) return;
            touched.Add(subj.Id);
            if (f.At > subj.UpdatedAt) subj.UpdatedAt = f.At;
            // The timeline: every fact of the payment's own chain, with its data (no payload is ever on this chain).
            o.Timeline.Add(new OpsTimelineEntry
            {
                SubjectId = subj.Id, StreamId = f.SubjectId, Seq = f.Seq, At = f.At, Kind = f.Kind, Code = f.Code, Actor = f.Actor,
                Instance = (string?)data["instance"], Step = (string?)data["step"], Activity = (string?)data["activity"], DataJson = CanonicalJson.Of(data),
            });
            switch (f.Kind)
            {
                case "status":
                    subj.Status ??= f.Code;
                    break;
                case "step":
                    if (Sources.Path(data, "writes.status.code") is JsonNode ws) subj.Status = Sources.Text(ws);
                    else if (Sources.Path(data, "status.code") is JsonNode sc) subj.Status = Sources.Text(sc);
                    if ((string?)data["instance"] is string inst && !inst.Contains('/'))
                    {
                        subj.LastStep = (string?)data["step"];
                        subj.LastOutcome = f.Code;
                    }
                    break;
                case "hold":
                    await ApplyHold(subj, f, data, ct);
                    break;
                case "operator":
                    await ApplyAction(subj, f, data, ct);
                    break;
            }
        }

        async Task ApplyHold(OpsSubject subj, FactRow f, JsonObject data, CancellationToken ct)
        {
            var id = (string)data["hold"]!;
            var h = o.Holds.Local.FirstOrDefault(x => x.Id == id) ?? await o.Holds.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (f.Code == "opened")
            {
                if (h is null)
                    o.Holds.Add(h = new OpsHold { Id = id, SubjectId = subj.Id, Tenant = subj.Tenant, Kind = (string?)data["kind"] ?? "approval", Activity = (string?)data["activity"] ?? "", OpenedAt = f.At });
                h.Status = "open";
                h.Reason = (string?)data["reason"];
                subj.Hold = "open";
                subj.HoldReason = h.Reason;
                return;
            }
            if (h is null) return;
            h.Status = f.Code == "expired" ? "expired" : "decided";
            h.Decision = (string?)data["decision"] ?? f.Code;
            h.DecidedAt = f.At;
            h.DecidedBy = (string?)data["by"];
            if (!o.Holds.Local.Any(x => x.SubjectId == subj.Id && x.Status == "open" && x.Id != id)
                && !await o.Holds.AnyAsync(x => x.SubjectId == subj.Id && x.Status == "open" && x.Id != id, ct))
                subj.Hold = null;
        }

        async Task ApplyAction(OpsSubject subj, FactRow f, JsonObject data, CancellationToken ct)
        {
            var id = (string)data["action"]!;
            var a = o.Actions.Local.FirstOrDefault(x => x.Id == id) ?? await o.Actions.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null)
            {
                if (f.Code != "requested") return;
                o.Actions.Add(a = new OpsAction
                {
                    Id = id, SubjectId = subj.Id, Tenant = subj.Tenant, Action = (string?)data["name"] ?? "", HoldId = (string?)data["hold"],
                    RequestedBy = f.Actor, RequestedAt = f.At, Remarks = (string?)data["remarks"], SecondApprover = (string?)data["secondApprover"],
                    Status = data["secondApprover"] is null ? "requested" : "pending-approval",
                });
                return;
            }
            switch (f.Code)
            {
                case "approved": a.ApprovedBy = f.Actor; a.ApprovedAt = f.At; a.Status = "approved"; break;
                case "rejected": a.ApprovedBy = f.Actor; a.ApprovedAt = f.At; a.Status = "rejected"; a.Result = (string?)data["remarks"]; break;
                case "done": a.Status = "done"; a.Result = (string?)data["result"]; break;
                case "refused": a.Status = "refused"; a.Result = (string?)data["result"]; break;
            }
        }

        /// <summary>A payload fact: only the lines' own facts (items[i].facts) are read; the payload itself is never kept.</summary>
        async Task ApplyPayloadFact(OpsStream st, FactRow f, JsonObject data, CancellationToken ct)
        {
            var pending = JsonNode.Parse(st.PendingJson) as JsonObject ?? new JsonObject();
            switch (f.Code)
            {
                case "accepted":
                    st.SubjectId = (string?)data["bulk"] ?? (string?)data["payment"];
                    // What the payload held before it was accepted (its lines' facts at creation).
                    if (pending["created"] is JsonArray created && st.SubjectId is not null) await LineFacts(st.SubjectId, created, ct);
                    pending.Remove("created");
                    break;
                case "create":
                case "fork":
                {
                    if (f.Code == "fork" && (string?)data["from"] is string from) st.SubjectId ??= (await Stream(from, ct)).SubjectId;
                    var items = new JsonArray();
                    var any = false;
                    foreach (var it in (Sources.Path(data, "payload.items") as JsonArray ?? []))
                    {
                        var facts = (it as JsonObject)?["facts"]?.DeepClone();
                        any |= facts is not null;
                        items.Add(facts);
                    }
                    if (!any) break;
                    if (st.SubjectId is null) pending["created"] = items;
                    else await LineFacts(st.SubjectId, items, ct);
                    break;
                }
                case "staged":
                {
                    var keep = new JsonArray();
                    foreach (var w in (data["writes"] as JsonArray ?? []).OfType<JsonObject>())
                        if (ItemFacts.IsMatch((string?)w["path"] ?? "")) keep.Add(w.DeepClone());
                    if (keep.Count > 0) (pending["staged"] ??= new JsonObject())[(string)data["token"]!] = keep;
                    break;
                }
                case "commit":
                {
                    var token = (string)data["token"]!;
                    if (pending["staged"] is JsonObject staged && staged[token] is JsonArray ws)
                    {
                        staged.Remove(token);
                        if (st.SubjectId is not null) await LineWrites(st.SubjectId, ws.OfType<JsonObject>().Select(w => ((string)w["path"]!, w["value"])), ct);
                    }
                    break;
                }
                case "copy":
                    if (st.SubjectId is not null && data["values"] is JsonObject values)
                        await LineWrites(st.SubjectId, values.Where(kv => ItemFacts.IsMatch(kv.Key)).Select(kv => (kv.Key, kv.Value)), ct);
                    break;
            }
            st.PendingJson = CanonicalJson.Of(pending);
        }

        async Task<List<OpsLine>> LinesOf(string bulkId, CancellationToken ct)
        {
            var local = o.Lines.Local.Where(x => x.BulkId == bulkId).ToList();
            if (local.Count > 0) return local.OrderBy(x => x.LineNo).ToList();
            return await o.Lines.Where(x => x.BulkId == bulkId).OrderBy(x => x.LineNo).ToListAsync(ct);
        }

        async Task LineFacts(string subjectId, JsonArray items, CancellationToken ct)
        {
            var lines = await LinesOf(subjectId, ct);
            for (var i = 0; i < items.Count && i < lines.Count; i++)
                if (items[i] is JsonObject facts) lines[i].FactsJson = CanonicalJson.Of(facts);
            touched.Add(subjectId);
        }

        async Task LineWrites(string subjectId, IEnumerable<(string path, JsonNode? value)> writes, CancellationToken ct)
        {
            var lines = await LinesOf(subjectId, ct);
            foreach (var (path, value) in writes)
            {
                var m = ItemFacts.Match(path);
                if (!m.Success) continue;
                var i = int.Parse(m.Groups[1].Value);
                if (i >= lines.Count) continue;
                var facts = JsonNode.Parse(lines[i].FactsJson) as JsonObject ?? new JsonObject();
                var rest = path[m.Length..];
                if (rest.Length == 0) facts = value as JsonObject ?? new JsonObject();
                else Set(facts, rest, value?.DeepClone());
                lines[i].FactsJson = CanonicalJson.Of(facts);
            }
            touched.Add(subjectId);
        }

        static void Set(JsonObject o, string path, JsonNode? v)
        {
            var parts = path.Split('.');
            for (var i = 0; i < parts.Length - 1; i++) o = (o[parts[i]] as JsonObject) ?? (JsonObject)(o[parts[i]] = new JsonObject());
            o[parts[^1]] = v;
        }

        public async Task ApplyMessage(string id, CancellationToken ct)
        {
            if (o.Documents.Local.Any(x => x.Id == id) || await o.Documents.AnyAsync(x => x.Id == id, ct)) return;
            var m = await s.Messages.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.Id, x.SubjectId, x.Tenant, x.Scheme, x.Kind, x.Direction, x.FileName, Size = x.Content.Length, x.ContentHash, x.At }).FirstOrDefaultAsync(ct);
            if (m is null) return;
            // A message names its payment or bulk, or a payload reference that was accepted as one.
            var subject = m.SubjectId;
            if (subject is not null && !await s.Bulks.AnyAsync(b => b.Id == subject, ct) && !await s.Payments.AnyAsync(p => p.Id == subject, ct))
                subject = (await o.Streams.FirstOrDefaultAsync(x => x.StreamId == subject, ct))?.SubjectId;
            o.Documents.Add(new OpsDocument { Id = m.Id, SubjectId = subject, Tenant = m.Tenant, Scheme = m.Scheme, Kind = m.Kind, Direction = m.Direction.ToLowerInvariant(), FileName = m.FileName, Size = m.Size, ContentHash = m.ContentHash, At = m.At });
            if (subject is not null) touched.Add(subject);
        }

        /// <summary>A stored file (Issue I-97): a document of its payment or bulk, with its leg and the key it is signed by.</summary>
        public async Task ApplyFile(string id, CancellationToken ct)
        {
            if (o.Documents.Local.Any(x => x.Id == id) || await o.Documents.AnyAsync(x => x.Id == id, ct)) return;
            var f = await s.StoredFiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (f is null) return;
            var subject = f.SubjectId;
            if (subject is not null && !await s.Bulks.AnyAsync(b => b.Id == subject, ct) && !await s.Payments.AnyAsync(p => p.Id == subject, ct))
                subject = (await o.Streams.FirstOrDefaultAsync(x => x.StreamId == subject, ct))?.SubjectId ?? subject;
            o.Documents.Add(new OpsDocument
            {
                Id = f.Id, SubjectId = subject, Tenant = f.Tenant, Scheme = f.Scheme, Kind = f.Kind, Direction = f.Direction, FileName = f.FileName,
                Size = (int)Math.Min(f.Size, int.MaxValue), ContentHash = f.ContentHash, At = f.At, Source = "file", Leg = f.Leg, SigningKeyId = f.SigningKeyId,
            });
            if (subject is not null) touched.Add(subject);
        }

        /// <summary>Saves the facts applied, then works out each touched payment's fields from its views.</summary>
        public async Task Finish(CancellationToken ct)
        {
            await o.SaveChangesAsync(ct);
            foreach (var id in touched) await Derive(id, ct);
            await o.SaveChangesAsync(ct);
        }

        async Task Derive(string id, CancellationToken ct)
        {
            var subj = await o.Subjects.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (subj is null) return;
            var c = await Context(subj, ct);
            var (pview, sview) = (views.All.FirstOrDefault(v => v.Scheme is null), views.SchemeView(subj.Scheme));
            subj.View = string.Join(",", new[] { pview?.Key, sview?.Key }.OfType<string>());
            var fields = new JsonObject();
            var values = new List<(string field, JsonNode? v)>();
            foreach (var v in new[] { pview, sview }.OfType<OperationsView>())
            {
                var f = ViewEval.Fields(v.Subject?["fields"] as JsonArray, c);
                foreach (var (k, val) in f) { fields[k] = val?.DeepClone(); values.Add(($"{v.Name}:subject:{k}", val)); }
            }
            subj.FieldsJson = CanonicalJson.Of(fields);
            subj.FileName = Sources.Text(fields["fileName"]) ?? subj.FileName;

            // Legs: each instance that is not the payment's own flow, matched to the scheme view's legs by what it did.
            var existing = await o.Legs.Where(x => x.SubjectId == id).ToListAsync(ct);
            foreach (var group in c.Steps.Where(x => ((string?)x["instance"] ?? "").Contains('/')).GroupBy(x => (string)x["instance"]!))
            {
                var decl = sview?.Legs.FirstOrDefault(l => Matches(l, group.ToList()));
                if (decl is null) continue;
                var legId = $"{id}|{group.Key}";
                var leg = existing.FirstOrDefault(x => x.Id == legId);
                if (leg is null) { leg = new OpsLeg { Id = legId, SubjectId = id, Instance = group.Key }; o.Legs.Add(leg); existing.Add(leg); }
                leg.Scheme = subj.Scheme;
                leg.Leg = (string)decl["leg"]!;
                leg.Title = (string?)decl["title"] ?? leg.Leg;
                leg.Direction = (string?)Sources.Path(decl, "selectBy.direction") ?? subj.Direction;
                var last = group.Last();
                leg.LastStep = (string?)last["step"];
                leg.LastOutcome = (string?)last["outcome"] ?? (string?)last["code"];
                leg.UpdatedAt = DateTimeOffset.Parse((string)last["at"]!);
                var lc = new SourceContext { Subject = c.Subject, Bulk = c.Bulk, Payment = c.Payment, Ext = c.Ext, Steps = c.Steps, Hold = c.Hold, Documents = c.Documents, Lines = c.Lines, Instance = group.Key };
                var lf = ViewEval.Fields(decl["fields"] as JsonArray, lc);
                leg.FieldsJson = CanonicalJson.Of(lf);
                foreach (var (k, val) in lf) values.Add(($"{sview!.Name}:{leg.Leg}:{k}", val));
            }

            // Lines: the scheme view's line fields, masked.
            var lineFields = Sources.Path(sview?.Subject, "lines.fields") as JsonArray;
            if (lineFields is not null)
            {
                var lines = await o.Lines.Where(x => x.BulkId == id).OrderBy(x => x.LineNo).ToListAsync(ct);
                for (var i = 0; i < lines.Count && i < c.Lines.Count; i++)
                    lines[i].FieldsJson = CanonicalJson.Of(ViewEval.Fields(lineFields, c, c.Lines[i]));
            }

            // The values filters search: replaced whole for the payment.
            await o.FieldValues.Where(x => x.SubjectId == id).ExecuteDeleteAsync(ct);
            foreach (var (field, v) in values)
            {
                if (v is null or JsonObject or JsonArray) continue;
                var text = Sources.Text(v);
                // A number only where the value is one (an id written in digits stays text), and only what decimal(19,4) holds.
                decimal? number = v is JsonValue jv && jv.GetValueKind() == System.Text.Json.JsonValueKind.Number ? Sources.Number(v) : null;
                if (number is { } nb && Math.Abs(nb) >= 1_000_000_000_000_000m) number = null;
                o.FieldValues.Add(new OpsFieldValue { SubjectId = id, Field = field, Text = text is { Length: > 400 } ? text[..400] : text, Number = number });
            }
        }

        static bool Matches(JsonObject leg, List<JsonObject> steps)
        {
            var sel = leg["selectBy"] as JsonObject ?? new JsonObject();
            var header = steps.LastOrDefault()?["header"] as JsonObject;
            if ((string?)sel["direction"] is string d && (string?)header?["direction"] != d) return false;
            if ((string?)sel["kind"] is string k && (string?)header?["kind"] != k) return false;
            foreach (var want in (sel["steps"] as JsonArray ?? []).Select(x => (string)x!))
                if (!steps.Any(s => (string?)s["step"] == want || ((string?)s["step"] ?? "").StartsWith(want + ".", StringComparison.Ordinal))) return false;
            return true;
        }

        async Task<SourceContext> Context(OpsSubject subj, CancellationToken ct)
        {
            var subject = new JsonObject
            {
                ["id"] = subj.Id, ["kind"] = subj.Kind, ["tenant"] = subj.Tenant, ["scheme"] = subj.Scheme, ["direction"] = subj.Direction,
                ["status"] = subj.Status, ["lastOutcome"] = subj.LastOutcome, ["lastStep"] = subj.LastStep, ["amount"] = subj.Amount, ["currency"] = subj.Currency,
                ["count"] = subj.Count, ["acceptedAt"] = subj.AcceptedAt.ToString("O"), ["releaseId"] = subj.ReleaseId, ["hold"] = subj.Hold, ["holdReason"] = subj.HoldReason,
            };
            // A payment's end (I-97): its payment.end fact, and the duration derived from it, the end's time less the acceptance (I-100).
            var end = await o.Timeline.AsNoTracking().Where(x => x.SubjectId == subj.Id && x.Kind == "payment.end").OrderBy(x => x.Seq).FirstOrDefaultAsync(ct)
                ?? o.Timeline.Local.Where(x => x.SubjectId == subj.Id && x.Kind == "payment.end").OrderBy(x => x.Seq).FirstOrDefault();
            if (end is not null)
            {
                subject["endedAt"] = end.At.ToString("O");
                subject["endOutcome"] = end.Code;
                subject["duration"] = System.Xml.XmlConvert.ToString(end.At - subj.AcceptedAt);
            }
            JsonObject? bulk = null, payment = null;
            var ext = new JsonObject();
            var lines = new List<JsonObject>();
            if (subj.Kind == "bulk")
            {
                var b = await s.Bulks.AsNoTracking().FirstAsync(x => x.Id == subj.Id, ct);
                var debtor = await s.Parties.AsNoTracking().Where(x => x.Id == b.DebtorPartyId).Select(x => x.Name).FirstOrDefaultAsync(ct);
                // GrpHdr/InitgPty: the id the channel gave its user, never a name (R-02-18). The view masks it.
                var initiator = b.InitiatingPartyId is null ? null
                    : (await s.Parties.AsNoTracking().Where(x => x.Id == b.InitiatingPartyId).Select(x => x.OtherIdsJson).FirstOrDefaultAsync(ct)) is { } ids
                        ? (JsonNode.Parse(ids) as JsonArray)?.OfType<JsonObject>().Select(o => (string?)o["Id"]).FirstOrDefault() : null;
                bulk = new JsonObject
                {
                    ["id"] = b.Id, ["messageId"] = b.MessageId, ["paymentInformationId"] = b.PaymentInformationId, ["numberOfTransactions"] = b.NumberOfTransactions,
                    ["controlSum"] = b.ControlSum, ["requestedExecutionDate"] = b.RequestedExecutionDate?.ToString("yyyy-MM-dd"), ["creationDateTime"] = b.CreationDateTime.ToString("O"),
                    ["acceptedAt"] = b.AcceptedAt.ToString("O"), ["categoryPurposeCode"] = b.CategoryPurposeCode, ["releaseId"] = b.ReleaseId, ["debtorName"] = debtor,
                    ["debtorAccount"] = Account(b.DebtorAccount), ["debtorAgent"] = Agent(b.DebtorAgent),
                    ["initiatingParty"] = new JsonObject { ["id"] = initiator },
                };
                ext = JsonNode.Parse(b.SupplementaryJson) as JsonObject ?? new JsonObject();
                var rows = await s.Payments.AsNoTracking().Where(x => x.BulkId == subj.Id).OrderBy(x => x.LineNo).ToListAsync(ct);
                var facts = await o.Lines.AsNoTracking().Where(x => x.BulkId == subj.Id).ToDictionaryAsync(x => x.Id, x => x.FactsJson, ct);
                foreach (var l in o.Lines.Local.Where(x => x.BulkId == subj.Id)) facts[l.Id] = l.FactsJson;
                foreach (var r in rows) lines.Add(new JsonObject
                {
                    ["id"] = r.Id, ["lineNo"] = r.LineNo, ["endToEndId"] = r.EndToEndId, ["amount"] = r.Amount, ["currency"] = r.Currency,
                    ["creditorAccount"] = Account(r.CreditorAccount), ["creditorAgent"] = Agent(r.CreditorAgent),
                    ["ext"] = JsonNode.Parse(r.SupplementaryJson), ["facts"] = JsonNode.Parse(facts.GetValueOrDefault(r.Id, "{}")),
                });
            }
            else
            {
                var p = await s.Payments.AsNoTracking().FirstAsync(x => x.Id == subj.Id, ct);
                payment = new JsonObject
                {
                    ["id"] = p.Id, ["endToEndId"] = p.EndToEndId, ["instructionId"] = p.InstructionId, ["transactionId"] = p.TransactionId, ["uetr"] = p.Uetr,
                    ["amount"] = p.Amount, ["currency"] = p.Currency, ["settlementDate"] = p.SettlementDate?.ToString("yyyy-MM-dd"), ["purposeCode"] = p.PurposeCode,
                    ["creditorAccount"] = Account(p.CreditorAccount), ["creditorAgent"] = Agent(p.CreditorAgent), ["debtorAccount"] = Account(p.DebtorAccount), ["debtorAgent"] = Agent(p.DebtorAgent),
                };
                ext = JsonNode.Parse(p.SupplementaryJson) as JsonObject ?? new JsonObject();
            }
            var steps = await o.Timeline.AsNoTracking().Where(x => x.SubjectId == subj.Id && x.Kind == "step").OrderBy(x => x.Seq).ToListAsync(ct);
            var stepObjs = steps.Select(t =>
            {
                var d = JsonNode.Parse(t.DataJson) as JsonObject ?? new JsonObject();
                d["at"] = t.At.ToString("O");
                d["code"] = t.Code;
                return d;
            }).ToList();
            var hold = await o.Holds.AsNoTracking().Where(x => x.SubjectId == subj.Id).OrderByDescending(x => x.OpenedAt).FirstOrDefaultAsync(ct);
            var docs = await o.Documents.AsNoTracking().Where(x => x.SubjectId == subj.Id).OrderBy(x => x.At).ToListAsync(ct);
            return new SourceContext
            {
                Subject = subject, Bulk = bulk, Payment = payment, Ext = ext, Steps = stepObjs, Lines = lines,
                Hold = hold is null ? null : new JsonObject
                {
                    ["id"] = hold.Id, ["kind"] = hold.Kind, ["status"] = hold.Status, ["reason"] = hold.Reason, ["decision"] = hold.Decision,
                    ["openedAt"] = hold.OpenedAt.ToString("O"), ["decidedAt"] = hold.DecidedAt?.ToString("O"), ["decidedBy"] = hold.DecidedBy,
                },
                Documents = docs.Select(d => new JsonObject { ["kind"] = d.Kind, ["fileName"] = d.FileName, ["at"] = d.At.ToString("O"), ["size"] = d.Size, ["direction"] = d.Direction }).ToList(),
            };
        }

        static JsonObject Account(AccountCols a) => new() { ["iban"] = a.Iban, ["other"] = a.OtherId, ["currency"] = a.Currency, ["type"] = a.Type };
        static JsonObject Agent(AgentCols a) => new() { ["bicfi"] = a.Bicfi, ["memberId"] = a.MemberId, ["clearingSystem"] = a.ClearingSystem, ["name"] = a.Name };
    }
}

public sealed record Lag(int Behind, double OldestSeconds, long Through, long Applied, DateTimeOffset? LastFactAt, DateTimeOffset? ProjectedAt, DateTimeOffset? RebuiltAt);
