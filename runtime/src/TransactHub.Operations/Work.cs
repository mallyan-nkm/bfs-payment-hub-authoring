using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Operations;

/// <summary>An item of an operator's work: a payment or bulk (its hold, its repair), or an action that waits for its second approver.</summary>
public sealed record WorkItem(string Kind, string Id)
{
    public const string Payment = "payment", Action = "action";
    public static readonly string[] Kinds = [Payment, Action];

    /// <summary>The item's own chain in the store: insert-only, one per item (R-05-71).</summary>
    public string Chain
    {
        get
        {
            var plain = $"work:{Kind}:{Id}";
            return plain.Length <= 64 ? plain : "work:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plain)))[..40];
        }
    }
}

/// <summary>Who holds an item now, from the facts of its chain.</summary>
public sealed record WorkState(WorkItem Item, string? Assignee, string? AssigneeName, int Level, bool Escalated, DateTimeOffset? Since, int Facts);

/// <summary>
/// Work management in the operations console (R-05-71): an item is assigned, escalated, reassigned or released, each a fact on the
/// item's own chain with who, when and why. An operator takes an unassigned item for themself; giving an item to someone else, or
/// taking it from the one who holds it, is a supervisor's; escalating hands the item to the supervisors, a level up, and needs a
/// reason. A bulk change applies the same rules to each item and records each item as its own fact; an item the rules refuse is
/// refused alone and the others go on. Assigning changes no payment and needs no bank approver: it is who works on what.
/// </summary>
public sealed class WorkQueue(StoreOptions store, StoreOptions ops, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public const string Kind = "work";
    public const string SubjectKind = "work";
    public static readonly string[] Ops = ["assign", "escalate", "release"];

    /// <summary>The tenant of an item, from the read model: the payment's, or the waiting action's.</summary>
    public async Task<string> TenantOf(WorkItem item, CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        var tenant = item.Kind switch
        {
            WorkItem.Payment => await o.Subjects.AsNoTracking().Where(s => s.Id == item.Id).Select(s => s.Tenant).FirstOrDefaultAsync(ct),
            WorkItem.Action => await o.Actions.AsNoTracking().Where(a => a.Id == item.Id).Select(a => a.Tenant).FirstOrDefaultAsync(ct),
            _ => throw new ActionRefused(400, $"No work item kind {item.Kind}: write payment or action", OpsCodes.NotFound),
        };
        return tenant ?? throw new ActionRefused(404, $"No {item.Kind} {item.Id}", OpsCodes.NotFound);
    }

    public async Task<WorkState> State(WorkItem item, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == item.Chain).OrderBy(f => f.Seq).ToListAsync(ct);
        return Fold(item, facts);
    }

    static WorkState Fold(WorkItem item, IEnumerable<FactRow> facts)
    {
        string? who = null, name = null;
        var level = 0;
        var escalated = false;
        DateTimeOffset? since = null;
        var n = 0;
        foreach (var f in facts)
        {
            n++;
            var d = JsonNode.Parse(f.DataJson) as JsonObject ?? new JsonObject();
            switch (f.Code)
            {
                case "assigned" or "reassigned": who = (string?)d["assignee"]; name = (string?)d["assigneeName"]; escalated = false; since = f.At; break;
                case "escalated": who = null; name = null; level = (int?)d["level"] ?? level + 1; escalated = true; since = f.At; break;
                case "released": who = null; name = null; since = f.At; break;
            }
        }
        return new WorkState(item, who, name, level, escalated, since, n);
    }

    /// <summary>The state of many items at once: one read of the facts of their chains.</summary>
    public async Task<Dictionary<WorkItem, WorkState>> States(IReadOnlyCollection<WorkItem> items, CancellationToken ct = default)
    {
        var chains = items.ToDictionary(i => i.Chain, i => i);
        await using var db = StoreDbContext.Open(store);
        var keys = chains.Keys.ToList();
        var facts = new List<FactRow>();
        foreach (var chunk in keys.Chunk(500))
            facts.AddRange(await db.Facts.AsNoTracking().Where(f => f.Kind == Kind && chunk.Contains(f.SubjectId)).ToListAsync(ct));
        var by = facts.GroupBy(f => f.SubjectId).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Seq).ToList());
        return items.ToDictionary(i => i, i => Fold(i, by.GetValueOrDefault(i.Chain) ?? []));
    }

    /// <summary>
    /// One change of one item. <paramref name="op"/>: <c>assign</c> (to <paramref name="assignee"/>, the caller when none is named),
    /// <c>escalate</c> or <c>release</c>.
    /// </summary>
    public async Task<JsonObject> Change(OperatorIdentity who, WorkItem item, string op, string? assignee, string? assigneeName, string? reason, CancellationToken ct = default)
    {
        var tenant = await TenantOf(item, ct);
        if (!who.Has(tenant, OperatorRoles.Operator)) throw new ActionRefused(403, $"No operator role for tenant {tenant}", OpsCodes.Forbidden);
        var supervisor = who.Has(tenant, OperatorRoles.Supervisor);
        var now = await State(item, ct);
        string code;
        var data = new JsonObject { ["item"] = item.Id, ["itemKind"] = item.Kind, ["tenant"] = tenant, ["by"] = who.DisplayName, ["why"] = reason };
        switch (op)
        {
            case "assign":
            {
                var to = string.IsNullOrWhiteSpace(assignee) ? who.Subject : assignee.Trim();
                if (now.Assignee == to) throw new ActionRefused(409, $"{item.Kind} {item.Id} is already with {to}", OpsCodes.AlreadyDecided);
                if (to != who.Subject && !supervisor) throw new ActionRefused(403, "Giving an item to someone else is a supervisor's", OpsCodes.Forbidden);
                if (now.Assignee is not null && now.Assignee != who.Subject && !supervisor) throw new ActionRefused(403, $"{item.Kind} {item.Id} is with {now.Assignee}; only a supervisor takes it from them", OpsCodes.Forbidden);
                if (now.Escalated && !supervisor) throw new ActionRefused(403, $"{item.Kind} {item.Id} is escalated to the supervisors", OpsCodes.Forbidden);
                code = now.Assignee is null ? "assigned" : "reassigned";
                if (code == "reassigned" && string.IsNullOrWhiteSpace(reason)) throw new ActionRefused(400, "Reassigning needs a reason", OpsCodes.RemarksRequired);
                data["assignee"] = to;
                data["assigneeName"] = to == who.Subject ? who.DisplayName : (assigneeName ?? to);
                if (now.Assignee is not null) data["from"] = now.Assignee;
                break;
            }
            case "escalate":
                if (string.IsNullOrWhiteSpace(reason)) throw new ActionRefused(400, "Escalating needs a reason", OpsCodes.RemarksRequired);
                if (now.Assignee is not null && now.Assignee != who.Subject && !supervisor) throw new ActionRefused(403, $"{item.Kind} {item.Id} is with {now.Assignee}; only they or a supervisor escalate it", OpsCodes.Forbidden);
                code = "escalated";
                data["to"] = OperatorRoles.Supervisor;
                data["level"] = now.Level + 1;
                if (now.Assignee is not null) data["from"] = now.Assignee;
                break;
            case "release":
                if (now.Assignee is null) throw new ActionRefused(409, $"{item.Kind} {item.Id} is with nobody", OpsCodes.AlreadyDecided);
                if (now.Assignee != who.Subject && !supervisor) throw new ActionRefused(403, $"{item.Kind} {item.Id} is with {now.Assignee}; only they or a supervisor release it", OpsCodes.Forbidden);
                code = "released";
                data["from"] = now.Assignee;
                break;
            default:
                throw new ActionRefused(400, $"No work change {op}: write assign, escalate or release", OpsCodes.ActionNotOffered);
        }
        await using var db = StoreDbContext.Open(store);
        var fact = await new FactLog(db, clock).Append(item.Chain, SubjectKind, new NewFact(Kind, code, data, Actor: who.Actor), ct);
        var after = await State(item, ct);
        return Show(after, fact.Seq);
    }

    /// <summary>A change of many items: each as <see cref="Change"/> would make it, each its own fact; a refusal stays with its item.</summary>
    public async Task<JsonObject> Bulk(OperatorIdentity who, IReadOnlyList<WorkItem> items, string op, string? assignee, string? assigneeName, string? reason, CancellationToken ct = default)
    {
        if (items.Count == 0) throw new ActionRefused(400, "Select at least one item", OpsCodes.ActionNotOffered);
        if (items.Count > 500) throw new ActionRefused(400, "A bulk change takes at most 500 items", OpsCodes.ActionNotOffered);
        var results = new JsonArray();
        var done = 0;
        foreach (var item in items.Distinct())
        {
            try { results.Add(new JsonObject { ["item"] = item.Id, ["itemKind"] = item.Kind, ["ok"] = true, ["state"] = await Change(who, item, op, assignee, assigneeName, reason, ct) }); done++; }
            catch (ActionRefused e) { results.Add(new JsonObject { ["item"] = item.Id, ["itemKind"] = item.Kind, ["ok"] = false, ["status"] = e.Status, ["error"] = e.Message, ["code"] = e.Code }); }
        }
        return new JsonObject { ["op"] = op, ["done"] = done, ["refused"] = results.Count - done, ["results"] = results };
    }

    /// <summary>An item's history: every fact of its chain.</summary>
    public async Task<JsonArray> History(WorkItem item, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == item.Chain).OrderBy(f => f.Seq).ToListAsync(ct);
        return new JsonArray(facts.Select(f => (JsonNode)new JsonObject { ["seq"] = f.Seq, ["code"] = f.Code, ["at"] = f.At.ToString("O"), ["actor"] = f.Actor, ["data"] = JsonNode.Parse(f.DataJson) }).ToArray());
    }

    /// <summary>
    /// The queue: the payments of the caller's tenants that wait for a person (an open hold) and the actions that wait for a second
    /// approver, each with who holds it. <paramref name="filter"/>: <c>mine</c>, <c>unassigned</c>, <c>escalated</c> or <c>all</c>.
    /// </summary>
    public async Task<JsonObject> Queue(OperatorIdentity who, IReadOnlyCollection<string> tenants, string filter, CancellationToken ct = default)
    {
        await using var o = OpsDbContext.Open(ops);
        var held = await o.Subjects.AsNoTracking().Where(s => tenants.Contains(s.Tenant) && s.Hold == "open").OrderBy(s => s.UpdatedAt).Take(2000).ToListAsync(ct);
        var waiting = await o.Actions.AsNoTracking().Where(a => tenants.Contains(a.Tenant) && a.Status == "pending-approval").OrderBy(a => a.RequestedAt).Take(2000).ToListAsync(ct);
        var items = held.Select(s => new WorkItem(WorkItem.Payment, s.Id)).Concat(waiting.Select(a => new WorkItem(WorkItem.Action, a.Id))).ToList();
        var states = await States(items, ct);
        var rows = new List<JsonObject>();
        foreach (var s in held)
        {
            var st = states[new WorkItem(WorkItem.Payment, s.Id)];
            rows.Add(Row(st, s.Tenant, s.Scheme, s.Id, s.HoldReason ?? "hold", s.Amount, s.Currency, s.UpdatedAt));
        }
        foreach (var a in waiting)
        {
            var st = states[new WorkItem(WorkItem.Action, a.Id)];
            var row = Row(st, a.Tenant, null, a.SubjectId, $"{a.Action} waits for {a.SecondApprover ?? "a second approver"}", null, null, a.RequestedAt);
            row["requestedBy"] = a.RequestedBy;
            row["action"] = a.Action;
            rows.Add(row);
        }
        IEnumerable<JsonObject> pick = filter switch
        {
            "mine" => rows.Where(r => (string?)r["assignee"] == who.Subject),
            "unassigned" => rows.Where(r => r["assignee"] is null && r["escalated"]?.GetValue<bool>() != true),
            "escalated" => rows.Where(r => r["escalated"]?.GetValue<bool>() == true),
            _ => rows,
        };
        var list = pick.ToList();
        return new JsonObject
        {
            ["filter"] = filter, ["total"] = list.Count,
            ["counts"] = new JsonObject
            {
                ["all"] = rows.Count, ["mine"] = rows.Count(r => (string?)r["assignee"] == who.Subject),
                ["unassigned"] = rows.Count(r => r["assignee"] is null && r["escalated"]?.GetValue<bool>() != true),
                ["escalated"] = rows.Count(r => r["escalated"]?.GetValue<bool>() == true),
            },
            ["rows"] = new JsonArray(list.Select(r => (JsonNode)r).ToArray()),
        };
    }

    static JsonObject Row(WorkState st, string tenant, string? scheme, string payment, string what, decimal? amount, string? currency, DateTimeOffset since) => new()
    {
        ["item"] = st.Item.Id, ["itemKind"] = st.Item.Kind, ["tenant"] = tenant, ["scheme"] = scheme, ["payment"] = payment, ["what"] = what,
        ["amount"] = amount, ["currency"] = currency, ["waitingSince"] = since.ToString("O"),
        ["assignee"] = st.Assignee, ["assigneeName"] = st.AssigneeName, ["escalated"] = st.Escalated, ["level"] = st.Level, ["assignedAt"] = st.Since?.ToString("O"),
    };

    static JsonObject Show(WorkState st, int seq) => new()
    {
        ["item"] = st.Item.Id, ["itemKind"] = st.Item.Kind, ["assignee"] = st.Assignee, ["assigneeName"] = st.AssigneeName,
        ["escalated"] = st.Escalated, ["level"] = st.Level, ["fact"] = seq,
    };

    /// <summary>The store's outbox head: what the live queue waits on (R-05-71). A new fact anywhere moves it.</summary>
    public async Task<long> Cursor(CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        return await db.Outbox.AsNoTracking().OrderByDescending(x => x.Id).Select(x => (long?)x.Id).FirstOrDefaultAsync(ct) ?? 0;
    }

    /// <summary>Waits until the outbox moves past <paramref name="after"/>, or <paramref name="wait"/> passes: a long poll for the live queue.</summary>
    public async Task<(long cursor, bool changed)> Changes(long after, TimeSpan wait, CancellationToken ct = default)
    {
        var until = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            var c = await Cursor(ct);
            if (c > after) return (c, true);
            if (DateTimeOffset.UtcNow >= until) return (c, false);
            await Task.Delay(250, ct);
        }
    }
}

/// <summary>
/// An operator's saved filters (R-05-71): named queries of the payment list, kept per person on a chain of their own in the store,
/// insert-only (a removal is a fact too). Only the person who saved a filter reads or removes it.
/// </summary>
public sealed class SavedFilters(StoreOptions store, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public const string Kind = "filter";

    static string Chain(OperatorIdentity who) => "filters:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(who.Actor)))[..40];

    public async Task<JsonArray> List(OperatorIdentity who, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == Chain(who)).OrderBy(f => f.Seq).ToListAsync(ct);
        var live = new Dictionary<string, JsonObject>();
        foreach (var f in facts)
        {
            var d = JsonNode.Parse(f.DataJson)!.AsObject();
            var id = (string)d["id"]!;
            if (f.Code == "saved") live[id] = new JsonObject { ["id"] = id, ["name"] = d["name"]?.DeepClone(), ["query"] = d["query"]?.DeepClone(), ["savedAt"] = f.At.ToString("O") };
            else if (f.Code == "removed") live.Remove(id);
        }
        return new JsonArray(live.Values.OrderBy(v => (string?)v["name"], StringComparer.OrdinalIgnoreCase).Select(v => (JsonNode)v).ToArray());
    }

    public async Task<JsonObject> Save(OperatorIdentity who, string? name, JsonObject? query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ActionRefused(400, "A saved filter needs a name of at most 80 characters", OpsCodes.RemarksRequired);
        var q = new JsonObject();
        foreach (var (k, v) in query ?? new JsonObject())
        {
            if (q.Count >= 40 || k.Length > 120) throw new ActionRefused(400, "A saved filter holds at most 40 terms", OpsCodes.ActionNotOffered);
            var s = v is JsonValue jv ? jv.ToString() : v?.ToJsonString() ?? "";
            if (s.Length > 200) throw new ActionRefused(400, $"The term {k} is longer than 200 characters", OpsCodes.ActionNotOffered);
            if (s.Length > 0) q[k] = s;
        }
        var id = Guid.NewGuid().ToString("N")[..12];
        await using var db = StoreDbContext.Open(store);
        await new FactLog(db, clock).Append(Chain(who), "filters", new NewFact(Kind, "saved", new JsonObject { ["id"] = id, ["name"] = name.Trim(), ["query"] = q }, Actor: who.Actor), ct);
        return new JsonObject { ["id"] = id, ["name"] = name.Trim(), ["query"] = q.DeepClone() };
    }

    public async Task Remove(OperatorIdentity who, string id, CancellationToken ct = default)
    {
        if (!(await List(who, ct)).Any(x => (string?)x?["id"] == id)) throw new ActionRefused(404, $"You have no saved filter {id}", OpsCodes.NotFound);
        await using var db = StoreDbContext.Open(store);
        await new FactLog(db, clock).Append(Chain(who), "filters", new NewFact(Kind, "removed", new JsonObject { ["id"] = id }, Actor: who.Actor), ct);
    }
}
