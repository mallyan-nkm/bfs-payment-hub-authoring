using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Operations;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Service;

/// <summary>
/// A troubleshooting action the service console offers (spec R-09-43, design/05 section 5.11). <paramref name="Path"/> says
/// how far it reaches: <c>built</c> end to end (every action now, Issue I-101), or <c>missing</c>: recorded, permitted and
/// audited, then refused naming what is missing.
/// </summary>
public sealed record ActionSpec(string Id, string Title, string Target, bool Risky, string Path, string Does, string? Missing = null);

public sealed record ActionAsk(string Action, string? Target, string? Reason, JsonObject? Parameters);

/// <summary>
/// The service console's actions: each needs a person's confirmation with a reason, is a fact with who, when and why, and one
/// that changes the system (<see cref="ActionSpec.Risky"/>: every action but re-verifying a package and replaying the read
/// model from its checkpoint, which the runtime does by itself) is carried out only when a bank approver, another person,
/// consents (owner's decision of 23 November 2026, R-09-77). Installing a version and restoring a backup are actions of the
/// same book, so they wait for the same consent.
/// </summary>
public sealed class ActionBook(StoreOptions store, ServiceAudit audit)
{
    const string I101 = "Issue I-101";

    public static readonly ActionSpec[] Catalogue =
    [
        new("install", "Install a version from GitHub", "tag", true, "built", "Downloads a signed version (a workflow package, a distribution or a configuration release), verifies it and stores it once (R-09-40)."),
        new("put-in-force", "Put a configuration release in force", "release", true, "built", "Puts an installed configuration release's values, port bindings, calendars and reference tables in force, once its signature and the parameter contract of every distribution not removed are checked again (R-09-111)."),
                new("restore", "Restore a backup", "backup", true, "built", "Restores a verified, signed backup into an empty or test runtime: its releases installed again from GitHub, its activations and values (R-09-41)."),
        new("activate", "Activate a release from a date", "release", true, "built", "Records an activation of an installed release; the release active until then starts draining."),
        new("rollback", "Roll back to the previous release", "scope", true, "built", "Activates again, now, the release that was active before the current one."),
        new("remove", "Remove a release", "release", true, "built", "Records the release as removed; refused while it is active or a payment pinned to it is open."),
        new("drain", "Drain a distribution", "release", true, "built", "Stops a distribution taking new payments; its open payments run to their end on it."),
        new("reverify-package", "Re-verify a package", "release", false, "built", "Verifies a stored package again against the signers trusted now, and records the verification on the release's chain."),
        new("rotate-trusted-key", "Rotate a trusted key", "key", true, "built", "Adds the studio's new public key to the trusted signers and retires the old one from a moment; writes the new list where TH_TRUSTED_SIGNERS names a writable file."),
        new("rebuild-read-model", "Rebuild the read model", "read-model", true, "built", "Throws the operations read model away and replays every fact from the outbox. Changes nothing in the store."),
        new("replay-read-model", "Replay the read model from its checkpoint", "read-model", false, "built", "Applies the outbox from the read model's checkpoint now. A fact already applied is not applied again (by its number on its chain)."),
        new("release-hold", "Release a hold", "payment", true, "built", "Decides the payment's open hold (approved for an approval hold, released for a repair hold) as the operations-queue port's decision."),
        new("requeue-dead-letter", "Requeue a dead-letter job (Flowable)", "job", true, "built", "Moves a Flowable dead-letter job back to the executable jobs, through the Flowable REST server."),
        new("re-screen", "Re-screen a payment", "payment", true, "built", "Sends the re-screen signal to the payment's engine; its host delivers it and records the delivery on the payment's chain."),
        new("retry-attempt", "Retry an attempt", "payment", true, "built", "Gives the payment's parked step one more attempt: the retry control through the engine's host (R-05-55)."),
        new("redrive-workflow", "Re-drive a stuck workflow", "payment", true, "built", "Runs the payment's parked step again with its full retry policy: the re-drive control through the engine's host (R-05-55)."),
        new("reset-activity", "Reset an activity (Temporal)", "payment", true, "built", "Runs the parked activity again from its first attempt, as Temporal's reset to the failed activity's task would; on every engine the re-drive control (R-05-55)."),
        new("resend-file", "Resend a file to the Central Bank", "payment", true, "built", "Puts the same bytes the exchange sent, as the store kept them, on the file-transfer port again; idempotent by the file's id (R-05-58)."),
        new("expire-hold", "Expire a hold", "payment", true, "built", "Ends the payment's open hold as expired before its expiry; the decision goes to the engine, which awaits it (R-05-56)."),
        new("reconcile-posting", "Reconcile an unknown posting with core banking", "payment", true, "built", "Asks core banking for the posting by its reference: posted, the parked posting step is re-driven and core banking answers the posting it has; not posted, it is retried (R-08-19)."),
        new("pause-intake", "Pause intake for a scheme", "scheme", true, "built", "Stops new payments of a scheme for a bank at the front door: the runtime refuses to accept them until intake is resumed (R-09-62)."),
        new("resume-intake", "Resume intake for a scheme", "scheme", true, "built", "Lets new payments of a scheme for a bank in again (R-09-62)."),
        new("drain-worker", "Drain a worker", "worker", true, "built", "Stops a worker taking new attempts; the attempts it has run to their end (R-09-63)."),
        new("restart-worker", "Restart a worker", "worker", true, "built", "Drains a worker and restarts it with the same package and version, on the same port (R-09-63)."),
    ];

    readonly Dictionary<string, Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>>> runners = new(StringComparer.Ordinal);

    public void Run(string action, Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> runner) => runners[action] = runner;

    readonly Dictionary<string, Func<ServiceIdentity, ActionAsk, CancellationToken, Task>> checks = new(StringComparer.Ordinal);

    /// <summary>A check run when the action is asked for, before it waits for consent; a refusal there refuses the request.</summary>
    public void Check(string action, Func<ServiceIdentity, ActionAsk, CancellationToken, Task> check) => checks[action] = check;

    public static ActionSpec Spec(string action) => Catalogue.FirstOrDefault(a => a.Id == action) ?? throw new ServiceRefused(404, ErrorCodes.ActionNotOffered, $"No troubleshooting action {action}");

    /// <summary>A person asks for an action. One that changes nothing the runtime would not: carried out now. Otherwise it waits for a bank approver's consent.</summary>
    public async Task<JsonObject> Request(ServiceIdentity who, ActionAsk ask, CancellationToken ct = default)
    {
        var spec = Spec(ask.Action);
        if (!who.Has(ServiceRoles.Engineer)) throw new ServiceRefused(403, ErrorCodes.Forbidden, $"{ask.Action} needs the {ServiceRoles.Engineer} role");
        if (string.IsNullOrWhiteSpace(ask.Reason)) throw new ServiceRefused(400, ErrorCodes.RemarksRequired, $"{ask.Action} needs a reason: who asked and why");
        if (spec.Target != "read-model" && string.IsNullOrWhiteSpace(ask.Target)) throw new ServiceRefused(400, ErrorCodes.ActionNotOffered, $"{ask.Action} needs a {spec.Target}");
        // What is checked before an action waits for consent: a version that does not verify is refused now, not at the approval.
        if (checks.TryGetValue(ask.Action, out var check)) await check(who, ask, ct);
        var id = Guid.NewGuid().ToString("N");
        var (payment, kind) = spec.Target == "payment" ? await SubjectOf(ask.Target!, ct) : (null, null);
        await audit.Record("action.requested", who, ask.Reason, new JsonObject
        {
            ["action"] = id, ["name"] = ask.Action, ["target"] = ask.Target, ["parameters"] = Masked.Json(ask.Parameters), ["risky"] = spec.Risky, ["path"] = spec.Path,
        }, payment: payment, paymentKind: kind, ct: ct);
        if (spec.Risky) return new JsonObject { ["action"] = id, ["name"] = ask.Action, ["target"] = ask.Target, ["status"] = "pending-approval", ["needs"] = ServiceRoles.BankApprover, ["code"] = ErrorCodes.ApproverRequired };
        return await Execute(who, id, ask, spec, payment, kind, ct);
    }

    /// <summary>A bank approver consents to (or refuses) an action another person asked for (R-09-77).</summary>
    public async Task<JsonObject> Decide(ServiceIdentity who, string actionId, bool approve, string? reason, CancellationToken ct = default)
    {
        if (!who.MayConsent) throw new ServiceRefused(403, ErrorCodes.Forbidden, $"Consenting needs the {ServiceRoles.BankApprover} role");
        if (string.IsNullOrWhiteSpace(reason)) throw new ServiceRefused(400, ErrorCodes.RemarksRequired, "A decision needs a reason");
        var facts = await Facts(actionId, ct);
        var req = facts.FirstOrDefault(f => f.Code == "action.requested") ?? throw new ServiceRefused(404, ErrorCodes.ActionNotOffered, $"No action {actionId}");
        if (facts.Count > 1) throw new ServiceRefused(409, ErrorCodes.AlreadyDecided, $"Action {actionId} is already {facts[^1].Code}");
        if (req.Actor == who.Actor) throw new ServiceRefused(403, ErrorCodes.MakerApproves, "The person who asked for an action may not approve it");
        var d = (JsonObject)JsonNode.Parse(req.DataJson)!;
        var ask = new ActionAsk((string)d["name"]!, (string?)d["target"], (string?)d["why"], d["parameters"] as JsonObject);
        var spec = Spec(ask.Action);
        var (payment, kind) = spec.Target == "payment" ? await SubjectOf(ask.Target!, ct) : (null, null);
        await audit.Record(approve ? "action.approved" : "action.rejected", who, reason, new JsonObject { ["action"] = actionId, ["name"] = ask.Action, ["target"] = ask.Target, ["requestedBy"] = req.Actor, ["consent"] = approve }, payment: payment, paymentKind: kind, ct: ct);
        if (!approve) return new JsonObject { ["action"] = actionId, ["name"] = ask.Action, ["status"] = "rejected" };
        return await Execute(who, actionId, ask, spec, payment, kind, ct);
    }

    /// <summary>Risky actions that wait for an approver, oldest first.</summary>
    public async Task<JsonArray> Pending(CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == ServiceAudit.Subject && f.Kind == ServiceAudit.Kind && f.Code!.StartsWith("action.")).OrderBy(f => f.Seq).ToListAsync(ct);
        var by = facts.GroupBy(f => (string?)JsonNode.Parse(f.DataJson)?["action"]).Where(g => g.Count() == 1 && g.First().Code == "action.requested");
        return new JsonArray(by.Select(g => g.First()).Where(f => JsonNode.Parse(f.DataJson)?["risky"]?.GetValue<bool>() == true).Select(f => (JsonNode)ServiceAudit.Show(f)).ToArray());
    }

    async Task<List<FactRow>> Facts(string actionId, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        var rows = await db.Facts.AsNoTracking().Where(f => f.SubjectId == ServiceAudit.Subject && f.Kind == ServiceAudit.Kind && f.Code!.StartsWith("action.")).OrderBy(f => f.Seq).ToListAsync(ct);
        return rows.Where(f => (string?)JsonNode.Parse(f.DataJson)?["action"] == actionId).ToList();
    }

    async Task<JsonObject> Execute(ServiceIdentity who, string id, ActionAsk ask, ActionSpec spec, string? payment, string? kind, CancellationToken ct)
    {
        string status;
        JsonObject result;
        string? code = null;
        int? httpStatus = null;
        if (spec.Path == "missing")
        {
            (status, code) = ("refused", ErrorCodes.NoEnginePath);
            result = new JsonObject { ["message"] = $"{ask.Action} is recorded and refused: {spec.Missing}" };
        }
        else if (!runners.TryGetValue(ask.Action, out var run))
        {
            (status, code) = ("refused", ErrorCodes.NoEnginePath);
            result = new JsonObject { ["message"] = $"{ask.Action} is not configured on this host" };
        }
        else
        {
            try { result = await run(who, ask, ct); status = "done"; }
            catch (ServiceRefused e) { (status, code, httpStatus) = ("refused", e.Code, e.Status); result = new JsonObject { ["message"] = e.Message }; }
            catch (Exception e) when (e is HttpRequestException or SignatureRejected or SecretUnavailable or InvalidOperationException or IOException)
            { (status, code) = ("refused", ErrorCodes.Of(e)); result = new JsonObject { ["message"] = Masked.Text(e.Message) }; }
        }
        await audit.Record($"action.{status}", who, ask.Reason, new JsonObject { ["action"] = id, ["name"] = ask.Action, ["error"] = code, ["result"] = result.DeepClone() }, payment: payment, paymentKind: kind, ct: ct);
        return new JsonObject { ["action"] = id, ["name"] = ask.Action, ["status"] = status, ["code"] = code, ["httpStatus"] = httpStatus, ["runbook"] = code is null ? null : ErrorEntry.RunbookLink(code), ["result"] = result };
    }

    async Task<(string?, string?)> SubjectOf(string id, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        if (await db.Bulks.AsNoTracking().AnyAsync(b => b.Id == id, ct)) return (id, "bulk");
        if (await db.Payments.AsNoTracking().AnyAsync(p => p.Id == id, ct)) return (id, "payment");
        throw new ServiceRefused(404, ErrorCodes.ActionNotOffered, $"No payment or bulk {id}");
    }

    /// <summary>The open hold of a payment, from the store's facts.</summary>
    public static async Task<(string id, string kind)?> OpenHold(StoreOptions store, string subjectId, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == subjectId && f.Kind == "hold").OrderBy(f => f.Seq).ToListAsync(ct);
        var closed = facts.Where(f => f.Code != "opened").Select(f => (string?)JsonNode.Parse(f.DataJson)?["hold"]).ToHashSet();
        var open = facts.LastOrDefault(f => f.Code == "opened" && !closed.Contains((string?)JsonNode.Parse(f.DataJson)?["hold"]));
        if (open is null) return null;
        var d = JsonNode.Parse(open.DataJson)!;
        return ((string)d["hold"]!, (string?)d["kind"] ?? "approval");
    }

    /// <summary>Release a hold: the decision the hold waits for (as an operator's would be), on the payment's chain and on its way to the engine.</summary>
    public static Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> ReleaseHold(StoreOptions store, TimeProvider clock, IEngineSignals? signals = null) => async (who, ask, ct) =>
    {
        var subject = ask.Target!;
        var open = await OpenHold(store, subject, ct) ?? throw new ServiceRefused(409, ErrorCodes.HoldClosed, $"{subject} has no open hold");
        var decision = open.kind == "repair" ? "released" : "approved";
        await using var db = StoreDbContext.Open(store);
        var kind = await db.Bulks.AsNoTracking().AnyAsync(b => b.Id == subject, ct) ? "bulk" : "payment";
        await new FactLog(db, clock).Append(subject, kind, new NewFact("hold", "decided", new JsonObject { ["hold"] = open.id, ["decision"] = decision, ["by"] = who.Actor, ["via"] = "service-console" }, Actor: who.Actor), ct);
        // The decision reaches the engine, which awaits it (R-05-56, Issue I-98).
        if (signals is not null)
        {
            var tenant = await db.Bulks.AsNoTracking().Where(b => b.Id == subject).Select(b => b.Tenant).FirstOrDefaultAsync(ct)
                ?? await db.Payments.AsNoTracking().Where(p => p.Id == subject).Select(p => p.Tenant).FirstOrDefaultAsync(ct) ?? "";
            await signals.Send(tenant, subject, null, "decide", Guid.NewGuid().ToString("N"), ct, new JsonObject { ["hold"] = open.id, ["decision"] = decision, ["by"] = who.Actor });
        }
        return new JsonObject { ["hold"] = open.id, ["decision"] = decision };
    };

    /// <summary>Re-screen: the signal on the outbox for the engine's host, which delivers it (R-05-57).</summary>
    public static Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> ReScreen(StoreOptions store, IEngineSignals signals) => Signal(store, signals, "re-screen");

    /// <summary>A signal or an operator's control, on the outbox for the payment's engine host (R-05-55, R-05-57).</summary>
    public static Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> Signal(StoreOptions store, IEngineSignals signals, string signal, bool needsParked = false, JsonObject? data = null) => async (who, ask, ct) =>
    {
        var (tenant, _) = await TenantOf(store, ask.Target!, who, ct);
        if (needsParked && !await Parked(store, ask.Target!, ct)) throw new ServiceRefused(409, ErrorCodes.NoParkedStep, $"{ask.Target} has no parked step: only a step that failed and parks takes {signal} (R-05-55)");
        var text = await signals.Send(tenant, ask.Target!, null, signal, Guid.NewGuid().ToString("N"), ct, data?.DeepClone() as JsonObject);
        return new JsonObject { ["signal"] = signal, ["result"] = text, ["queued"] = true };
    };

    static async Task<(string tenant, string kind)> TenantOf(StoreOptions store, string subject, ServiceIdentity who, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        var b = await db.Bulks.AsNoTracking().Where(x => x.Id == subject).Select(x => x.Tenant).FirstOrDefaultAsync(ct);
        var (tenant, kind) = b is not null ? (b, "bulk") : (await db.Payments.AsNoTracking().Where(p => p.Id == subject).Select(p => p.Tenant).FirstOrDefaultAsync(ct) ?? "", "payment");
        if (!who.Sees(tenant)) throw new ServiceRefused(403, ErrorCodes.Forbidden, $"No tenant {tenant} for {who.DisplayName}");
        return (tenant, kind);
    }

    /// <summary>Whether the payment's last step is parked (R-05-55): its last step fact is <c>stuck</c>.</summary>
    public static async Task<bool> Parked(StoreOptions store, string subject, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        return await db.Facts.AsNoTracking().Where(f => f.SubjectId == subject && f.Kind == "step").OrderByDescending(f => f.Seq).Select(f => f.Code).FirstOrDefaultAsync(ct) == "stuck";
    }

    /// <summary>Expire a hold: recorded expired, and the decision on its way to the engine (R-05-56).</summary>
    public static Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> ExpireHold(StoreOptions store, HoldExpiry expiry) => async (who, ask, ct) =>
    {
        var (_, kind) = await TenantOf(store, ask.Target!, who, ct);
        var open = await OpenHold(store, ask.Target!, ct) ?? throw new ServiceRefused(409, ErrorCodes.HoldClosed, $"{ask.Target} has no open hold");
        return await expiry.Expire(ask.Target!, kind, open.id, who.Actor, null, ask.Reason ?? "expired by a service engineer", ct);
    };

    /// <summary>Resend a file: the kept bytes on the file-transfer port again (R-05-58).</summary>
    public static Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> ResendFile(StoreOptions store, FileResend resend) => async (who, ask, ct) =>
    {
        var (_, kind) = await TenantOf(store, ask.Target!, who, ct);
        try { return await resend.Resend(ask.Target!, kind, (string?)ask.Parameters?["file"], who.Actor, Guid.NewGuid().ToString("N"), ct); }
        catch (ActionRefused e) { throw new ServiceRefused(e.Status, e.Code, e.Message); }
    };

    /// <summary>
    /// Reconcile an unknown posting (R-08-19): core banking is asked for the posting by its reference (the parameter, else the
    /// reference the payment's posting step wrote). Posted: the answer is recorded and the parked posting step is re-driven, so
    /// core banking answers the posting it has; not posted: it is retried. The answer is a fact on the payment's chain.
    /// </summary>
    public static Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> ReconcilePosting(StoreOptions store, IEngineSignals signals, Func<string, TransactHub.Ports.ICoreBanking?> coreBanking, TimeProvider clock) => async (who, ask, ct) =>
    {
        var (tenant, kind) = await TenantOf(store, ask.Target!, who, ct);
        var reference = (string?)ask.Parameters?["reference"] ?? await PostingReference(store, ask.Target!, ct)
            ?? throw new ServiceRefused(409, ErrorCodes.PostingUnknown, $"{ask.Target} has no posting reference: name it as the parameter reference");
        var cb = coreBanking(tenant) ?? throw new ServiceRefused(409, ErrorCodes.PostingUnknown, $"No core banking is bound for tenant {tenant} on this host");
        var answer = await cb.Inquire(reference, ct);
        var control = answer.Posted ? "redrive" : "retry";
        await using (var db = StoreDbContext.Open(store))
            await new FactLog(db, clock).Append(ask.Target!, kind, new NewFact("posting", "reconciled",
                new JsonObject { ["reference"] = reference, ["posted"] = answer.Posted, ["hostReference"] = answer.HostReference, ["then"] = control }, Actor: who.Actor), ct);
        var parked = await Parked(store, ask.Target!, ct);
        if (parked) await signals.Send(tenant, ask.Target!, null, control, Guid.NewGuid().ToString("N"), ct, new JsonObject { ["reconciled"] = answer.Posted ? "posted" : "not posted" });
        return new JsonObject { ["reference"] = reference, ["posted"] = answer.Posted, ["hostReference"] = answer.HostReference, ["then"] = parked ? control : "nothing parked" };
    };

    static async Task<string?> PostingReference(StoreOptions store, string subject, CancellationToken ct)
    {
        await using var db = StoreDbContext.Open(store);
        foreach (var d in await db.Facts.AsNoTracking().Where(f => f.SubjectId == subject && f.Kind == "step").OrderByDescending(f => f.Seq).Select(f => f.DataJson).Take(50).ToListAsync(ct))
            foreach (var (_, v) in (JsonNode.Parse(d)?["writes"] as JsonObject) ?? new JsonObject())
                if ((string?)(v as JsonObject)?["reference"] is { Length: > 0 } r) return r;
        return null;
    }

    /// <summary>The intake switch of a bank's scheme (R-09-62): a fact on the switch's own chain, read by the runtime when it accepts a payment.</summary>
    public static Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> Intake(StoreOptions store, bool pause, TimeProvider clock) => async (who, ask, ct) =>
    {
        var tenant = (string?)ask.Parameters?["tenant"] ?? throw new ServiceRefused(400, ErrorCodes.ActionNotOffered, "An intake switch needs the parameter tenant (the bank) beside the scheme");
        if (!who.Sees(tenant)) throw new ServiceRefused(403, ErrorCodes.Forbidden, $"No tenant {tenant} for {who.DisplayName}");
        await using var db = StoreDbContext.Open(store);
        await IntakeSwitch.Set(db, clock, tenant, ask.Target!, pause, who.Actor, ask.Reason, ct);
        return new JsonObject { ["tenant"] = tenant, ["scheme"] = ask.Target, ["intake"] = pause ? "paused" : "open" };
    };

    /// <summary>Drain or restart a worker through its own control (R-09-63): <c>POST /drain</c> or <c>POST /restart</c> on the worker's address.</summary>
    public static Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> Worker(string control, HttpMessageHandler? handler = null) => async (_, ask, ct) =>
    {
        if (!Uri.TryCreate(ask.Target, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) throw new ServiceRefused(400, ErrorCodes.ActionNotOffered, "A worker is named by its address, for example http://127.0.0.1:7401");
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(30);
        HttpResponseMessage res;
        try { res = await http.PostAsync(new Uri(url, control), new StringContent("{}", Encoding.UTF8, "application/json"), ct); }
        catch (HttpRequestException) { throw new ServiceRefused(502, ErrorCodes.WorkerUnreachable, $"The worker at {url} does not answer"); }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw new ServiceRefused(502, ErrorCodes.WorkerUnreachable, $"The worker at {url} answered {(int)res.StatusCode} to {control}");
            return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject ?? new JsonObject { ["worker"] = ask.Target, [control] = true };
        }
    };

    /// <summary>Rotate a trusted key: the new list, checked, written where the setting names a writable file.</summary>
    public static Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> RotateKey(string trustSetting, ISecretResolver secrets, bool writable, TimeProvider clock) => async (who, ask, ct) =>
    {
        var p = ask.Parameters ?? new JsonObject();
        var pem = (string?)p["publicKey"] ?? throw new ServiceRefused(400, ErrorCodes.TrustListInvalid, "rotate-trusted-key needs the new publicKey (PEM)");
        var retire = ask.Target!;
        var current = JsonNode.Parse(trustSetting.TrimStart().StartsWith('{') ? trustSetting : (await secrets.Resolve(trustSetting, ct)).Reveal()) as JsonObject ?? throw new ServiceRefused(422, ErrorCodes.TrustListInvalid, "The trusted signers are not JSON");
        using var key = System.Security.Cryptography.ECDsa.Create();
        try { key.ImportFromPem(pem); } catch (Exception e) when (e is ArgumentException or System.Security.Cryptography.CryptographicException) { throw new ServiceRefused(422, ErrorCodes.TrustListInvalid, "The new key is not a PEM EC public key"); }
        var newId = ReleaseSignature.KeyIdOf(key);
        var keys = current["keys"] as JsonArray ?? new JsonArray();
        if (!keys.Any(k => (string?)k?["keyId"] == retire)) throw new ServiceRefused(404, ErrorCodes.UntrustedKey, $"Key {retire} is not trusted here");
        var at = (string?)p["retireAt"] is { } r ? DateTimeOffset.Parse(r, System.Globalization.CultureInfo.InvariantCulture) : clock.GetUtcNow();
        foreach (var k in keys.OfType<JsonObject>().Where(k => (string?)k["keyId"] == retire)) k["retiredAt"] = at.ToString("O");
        if (!keys.Any(k => (string?)k?["keyId"] == newId)) keys.Add(new JsonObject { ["keyId"] = newId, ["publicKey"] = pem });
        current["keys"] = keys.DeepClone();
        var text = current.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        TrustedSigners.Parse(text);
        string? written = null;
        if (writable && !trustSetting.TrimStart().StartsWith('{') && SecretRef.IsRef(trustSetting) && SecretRef.Parse(trustSetting) is { Scheme: "file" } f)
        {
            await File.WriteAllTextAsync(f.Locator, text, ct);
            written = f.Locator;
        }
        return new JsonObject { ["added"] = newId, ["retired"] = retire, ["retiredAt"] = at.ToString("O"), ["written"] = written, ["trustedSigners"] = written is null ? JsonNode.Parse(text) : null };
    };

    /// <summary>Requeue a Flowable dead-letter job through the Flowable REST server's management API.</summary>
    public static Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> RequeueDeadLetter(string baseUrl, string user, string? passwordRef, ISecretResolver secrets, HttpMessageHandler? handler = null) => async (_, ask, ct) =>
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        var password = passwordRef is null ? "" : (await secrets.Resolve(passwordRef, ct)).Reveal();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/service/management/deadletter-jobs/{Uri.EscapeDataString(ask.Target!)}")
        {
            Content = new StringContent("{\"action\":\"move\"}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) throw new ServiceRefused(404, "flowable.DeadLetterJob", $"Flowable has no dead-letter job {ask.Target}");
        if (!res.IsSuccessStatusCode) throw new ServiceRefused(502, "flowable.Unreachable", $"Flowable answered {(int)res.StatusCode} to the requeue");
        return new JsonObject { ["job"] = ask.Target, ["requeued"] = true };
    };
}
