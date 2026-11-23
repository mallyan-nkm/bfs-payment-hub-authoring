using System.Security.Claims;
using System.Text.Json.Nodes;
using TransactHub.Auth;
using TransactHub.Operations;
using TransactHub.Operations.Registers;

namespace TransactHub.Operations.Api;

/// <summary>
/// The registers area (R-05-62 to R-05-64, R-09-73): the console's routes for people, under <c>/ops/api/registers</c>, and the
/// register API for the bank's systems, under <c>/api/registers</c>. People: any register reader of the tenant sees the records,
/// masked; a RegisterMaker drafts a change; a BankApprover who is not its maker consents to it or refuses it. Systems (core
/// banking, by client credentials): <c>register-client</c> looks records up by their key and lookup fields, lists and searches
/// them, drafts changes and syncs in bulk; every change it makes waits for a bank approver's consent like a person's.
/// </summary>
public static class RegisterEndpoints
{
    public sealed record ChangeRequest(string? Tenant, List<RecordInput>? Records, string? Remarks);
    public sealed record RecordInput(JsonObject? Fields, DateTimeOffset? ValidFrom);
    public sealed record SyncRequest(string? Reference, List<RecordInput>? Records, string? Remarks);
    public sealed record DecisionBody(bool Approve, string? Remarks);

    static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    static OperatorIdentity? Who(HttpContext http) => http.RequestServices.GetRequiredService<IOperatorIdentityResolver>().Resolve(http.User);

    static bool Reads(OperatorIdentity who, string tenant) => OperatorRoles.RegisterReaders.Any(r => who.Has(tenant, r));

    static string? Q(HttpContext http, string k) => http.Request.Query.TryGetValue(k, out var v) && v.ToString().Length > 0 ? v.ToString() : null;

    static DateTimeOffset AsOf(HttpContext http) => Q(http, "asOf") is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? (s.Length == 10 ? d.AddDays(1).AddTicks(-10) : d) : DateTimeOffset.UtcNow;

    /// <summary>The tenant a person acts in: the one named, which they must hold a role in, or the only one they hold one in.</summary>
    static (string? tenant, IResult? refused) PersonTenant(OperatorIdentity who, string? named, Func<OperatorIdentity, string, bool> may)
    {
        if (named is { Length: > 0 }) return may(who, named) ? (named, null) : (null, Problem(403, $"No register role for tenant {named}"));
        var mine = who.Tenants.Where(t => t != OperatorIdentity.AnyTenant && may(who, t)).ToList();
        return mine.Count == 1 ? (mine[0], null) : (null, mine.Count == 0 ? Problem(403, "No register role for this action in any tenant") : Problem(400, "Name the tenant"));
    }

    /// <summary>The tenant a system client acts in: the one named (header X-TH-Tenant or ?tenant=), or the only one its grant gives.</summary>
    static (string? tenant, IResult? refused) SystemTenant(HttpContext http, string role)
    {
        var named = http.Request.Headers["X-TH-Tenant"].ToString() is { Length: > 0 } h ? h : Q(http, "tenant");
        if (named is { Length: > 0 }) return RuntimeAuth.HasRoleFor(http.User, role, named) ? (named, null) : (null, Problem(403, $"{RuntimeAuth.Actor(http.User)} has no {role} role for {named}"));
        var mine = RuntimeAuth.RolesByTenant(http.User).Where(kv => kv.Key != "*" && kv.Value.Contains(role)).Select(kv => kv.Key).ToList();
        return mine.Count == 1 ? (mine[0], null) : (null, Problem(mine.Count == 0 ? 403 : 400, mine.Count == 0 ? $"{RuntimeAuth.Actor(http.User)} has no {role} role" : "Name the tenant (X-TH-Tenant)"));
    }

    static async Task<IResult> Guard(Func<Task<IResult>> f)
    {
        try { return await f(); }
        catch (RegisterRefused e) { return Problem(e.Status, e.Message); }
    }

    static List<RegisterInput> Inputs(List<RecordInput>? rs) => (rs ?? []).Select(r => new RegisterInput(r.Fields ?? new JsonObject(), r.ValidFrom)).ToList();

    static object KindJson(RegisterKind k) => new
    {
        name = k.Name, title = k.Title, description = k.Description, key = k.Key, titleField = k.TitleField,
        fields = k.Fields.Select(f => new { f.Id, f.Title, f.Type, f.Required, f.Personal, f.Mask, f.Lookup, f.Iso, f.References }),
    };

    /// <param name="systemReadsMasked">Issue I-149 (1), I-181: true makes the register API answer core banking masked, as the kind declares;
    /// false (the default) answers it unmasked, as the system the data came from.</param>
    public static void MapRegisters(this WebApplication app, bool systemReadsMasked = false)
    {
        var ops = app.MapGroup("/ops/api/registers");

        ops.MapGet("", (HttpContext http, RegisterService regs) =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            if (!who.Tenants.Any(t => Reads(who, t))) return Problem(403, "No register role");
            return Results.Ok(new
            {
                kinds = regs.Catalog.All.Select(KindJson),
                tenants = who.Tenants.Where(t => Reads(who, t)).ToDictionary(t => t, t => new { maker = who.Has(t, OperatorRoles.RegisterMaker), approver = who.Has(t, OperatorRoles.BankApprover) }),
            });
        });

        ops.MapGet("/changes/pending", (HttpContext http, RegisterService regs) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var tenants = who.Tenants.Where(t => who.Has(t, OperatorRoles.BankApprover) || who.Has(t, OperatorRoles.RegisterMaker)).ToList();
            var rows = await regs.Pending(tenants, masked: true, http.RequestAborted);
            return Results.Ok(rows.Select(r => { r["mayDecide"] = who.Has((string)r["tenant"]!, OperatorRoles.BankApprover) && (string?)r["madeBy"] != who.Actor; return r; }));
        }));

        ops.MapGet("/changes/{id}", (string id, HttpContext http, RegisterService regs) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var c = await regs.ChangeDetail(id, masked: true, http.RequestAborted);
            return c is null || !Reads(who, (string)c["tenant"]!) ? Problem(404, $"No register change {id} for your tenants") : Results.Ok(c);
        }));

        ops.MapPost("/changes/{id}/decision", (string id, DecisionBody body, HttpContext http, RegisterService regs, ILoggerFactory logs) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var c = await regs.Change(id, http.RequestAborted);
            if (c is null || !Reads(who, c.Tenant)) return Problem(404, $"No register change {id} for your tenants");
            if (!who.Has(c.Tenant, OperatorRoles.BankApprover)) return Problem(403, "Consent to a register change needs the bank approver's role");
            var d = await regs.Decide(id, who.Actor, body.Approve, body.Remarks, http.RequestAborted);
            logs.CreateLogger("TransactHub.Operations.Registers").LogWarning("Register change {Change} {Decision} by {Subject}", id, d.Decision, who.Subject);
            return Results.Ok(await regs.ChangeDetail(id, masked: true, http.RequestAborted));
        }));

        ops.MapGet("/{kind}", (string kind, HttpContext http, RegisterService regs) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var (tenant, refused) = PersonTenant(who, Q(http, "tenant"), Reads);
            if (refused is not null) return refused;
            var k = regs.Kind(kind);
            var page = int.TryParse(Q(http, "page"), out var p) ? p : 1;
            var size = int.TryParse(Q(http, "pageSize"), out var s) ? s : 25;
            var (total, rows) = await regs.Search(tenant!, kind, Q(http, "q"), AsOf(http), page, size, http.RequestAborted);
            return Results.Ok(new { tenant, total, page, rows = rows.Select(r => RegisterService.Show(k, r)) });
        }));

        ops.MapGet("/{kind}/{key}", (string kind, string key, HttpContext http, RegisterService regs) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var (tenant, refused) = PersonTenant(who, Q(http, "tenant"), Reads);
            if (refused is not null) return refused;
            var k = regs.Kind(kind);
            var current = await regs.InForce(tenant!, kind, key, AsOf(http), http.RequestAborted);
            var history = await regs.History(tenant!, kind, key, masked: true, http.RequestAborted);
            if (current is null && history.Count == 0) return Problem(404, $"No {k.Title} {key}");
            return Results.Ok(new { tenant, key, current = current is null ? null : RegisterService.Show(k, current), history });
        }));

        ops.MapPost("/{kind}/changes", (string kind, ChangeRequest body, HttpContext http, RegisterService regs) => Guard(async () =>
        {
            if (Who(http) is not { } who) return Problem(401, "Sign in");
            var (tenant, refused) = PersonTenant(who, body.Tenant, (w, t) => w.Has(t, OperatorRoles.RegisterMaker));
            if (refused is not null) return refused;
            var c = await regs.Draft(tenant!, kind, who.Actor, "console", Inputs(body.Records), body.Remarks, null, http.RequestAborted);
            return c is null ? Results.Ok(new { changed = false }) : Results.Json(await regs.ChangeDetail(c.Id, masked: true, http.RequestAborted), statusCode: 201);
        }));

        // ------------------------------------------------------------- the register API, for the bank's systems
        var api = app.MapGroup("/api/registers");
        string SystemActor(HttpContext http) => $"system:{RuntimeAuth.Actor(http.User)}";

        api.MapGet("/{kind}/{key}", (string kind, string key, HttpContext http, RegisterService regs) => Guard(async () =>
        {
            var (tenant, refused) = SystemTenant(http, SystemRoles.RegisterClient);
            if (refused is not null) return refused;
            var r = await regs.InForce(tenant!, kind, key, AsOf(http), http.RequestAborted);
            return r is null ? Problem(404, $"No {regs.Kind(kind).Title} {key} in force") : Results.Ok(new { tenant, key, record = systemReadsMasked ? RegisterService.Show(regs.Kind(kind), r) : r, masked = systemReadsMasked });
        }));

        api.MapGet("/{kind}", (string kind, HttpContext http, RegisterService regs) => Guard(async () =>
        {
            var (tenant, refused) = SystemTenant(http, SystemRoles.RegisterClient);
            if (refused is not null) return refused;
            var k = regs.Kind(kind);
            // A lookup by a declared field (?cif=, ?establishmentId=, ?accounts=), else a search and a page.
            foreach (var f in k.Lookups)
                if (Q(http, f.Id) is { } v)
                    return Results.Ok(new { tenant, by = f.Id, rows = Shown(await regs.FindBy(tenant!, kind, f.Id, v, AsOf(http), http.RequestAborted)), masked = systemReadsMasked });
            var (total, rows) = await regs.Search(tenant!, kind, Q(http, "q"), AsOf(http), int.TryParse(Q(http, "page"), out var p) ? p : 1, int.TryParse(Q(http, "pageSize"), out var s) ? s : 100, http.RequestAborted);
            return Results.Ok(new { tenant, total, rows = Shown(rows), masked = systemReadsMasked });
            List<JsonObject> Shown(List<JsonObject> rs) => systemReadsMasked ? rs.Select(r => RegisterService.Show(k, r)).ToList() : rs;
        }));

        api.MapPost("/{kind}/changes", (string kind, ChangeRequest body, HttpContext http, RegisterService regs) => Guard(async () =>
        {
            var (tenant, refused) = SystemTenant(http, SystemRoles.RegisterClient);
            if (refused is not null) return refused;
            var c = await regs.Draft(tenant!, kind, SystemActor(http), "api", Inputs(body.Records), body.Remarks, null, http.RequestAborted);
            return c is null ? Results.Ok(new { changed = false }) : Results.Json(new { changed = true, change = c.Id, state = "pending", count = c.Count }, statusCode: 202);
        }));

        // Bulk load or sync from core banking: every record as core banking holds it; the ones that differ become one change,
        // which waits for a bank approver's consent. Idempotent by the caller's reference.
        api.MapPost("/{kind}/sync", (string kind, SyncRequest body, HttpContext http, RegisterService regs) => Guard(async () =>
        {
            var (tenant, refused) = SystemTenant(http, SystemRoles.RegisterClient);
            if (refused is not null) return refused;
            if (string.IsNullOrWhiteSpace(body.Reference)) return Problem(400, "A sync names its reference, so that sending it again changes nothing");
            var inputs = Inputs(body.Records);
            var c = await regs.Draft(tenant!, kind, SystemActor(http), "sync", inputs, body.Remarks, body.Reference, http.RequestAborted);
            return Results.Json(new { reference = body.Reference, received = inputs.Count, drafted = c?.Count ?? 0, unchanged = inputs.Count - (c?.Count ?? 0), change = c?.Id, state = c is null ? "unchanged" : "pending" }, statusCode: c is null ? 200 : 202);
        }));
    }
}
