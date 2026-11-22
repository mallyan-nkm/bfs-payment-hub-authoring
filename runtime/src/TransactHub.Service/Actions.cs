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
/// how far it reaches today: <c>built</c> end to end, <c>outbox</c> recorded and queued for an engine host that does not read
/// it yet (Issue I-99), or <c>missing</c>: recorded, permitted and audited, then refused naming what is missing (Issue I-101).
/// </summary>
public sealed record ActionSpec(string Id, string Title, string Target, bool Risky, string Path, string Does, string? Missing = null);

public sealed record ActionAsk(string Action, string? Target, string? Reason, JsonObject? Parameters);

/// <summary>
/// The service console's troubleshooting actions: each needs a person's confirmation with a reason, is a fact with who, when
/// and why, and a risky one is carried out only when a ServiceApprover, another person, approves it (maker-checker).
/// </summary>
public sealed class ActionBook(StoreOptions store, ServiceAudit audit)
{
    const string I101 = "Issue I-101";

    public static readonly ActionSpec[] Catalogue =
    [
        new("activate", "Activate a release from a date", "release", true, "built", "Records an activation of an installed release; the release active until then starts draining."),
        new("rollback", "Roll back to the previous release", "scope", true, "built", "Activates again, now, the release that was active before the current one."),
        new("remove", "Remove a release", "release", true, "built", "Records the release as removed; refused while it is active or a payment pinned to it is open."),
        new("drain", "Drain a distribution", "release", true, "built", "Stops a distribution taking new payments; its open payments run to their end on it."),
        new("reverify-package", "Re-verify a package", "release", false, "built", "Verifies a stored package again against the signers trusted now, and records the verification on the release's chain."),
        new("rotate-trusted-key", "Rotate a trusted key", "key", true, "built", "Adds the studio's new public key to the trusted signers and retires the old one from a moment; writes the new list where TH_TRUSTED_SIGNERS names a writable file."),
        new("rebuild-read-model", "Rebuild the read model", "read-model", true, "built", "Throws the operations read model away and replays every fact from the outbox. Changes nothing in the store."),
        new("replay-read-model", "Replay the read model from its checkpoint", "read-model", false, "built", "Applies the outbox from the read model's checkpoint now. A fact already applied is not applied again (by its number on its chain)."),
        new("release-hold", "Release a hold", "payment", true, "built", "Decides the payment's open hold (approved for an approval hold, released for a repair hold) as the operations-queue port's decision."),
        new("requeue-dead-letter", "Requeue a dead-letter job (Flowable)", "job", false, "built", "Moves a Flowable dead-letter job back to the executable jobs, through the Flowable REST server."),
        new("re-screen", "Re-screen a payment", "payment", false, "outbox", "Sends the re-screen signal to the payment's engine host through the outbox.", "No engine host reads the engine-signal topic yet, so the running payment is not re-screened (Issue I-99)."),
        new("retry-attempt", "Retry an attempt", "payment", false, "missing", "Runs a failed activity attempt again.", $"retry is not in the DSL: no construct takes it, and the store does not record an instance's engine id ({I101}, I-99)."),
        new("redrive-workflow", "Re-drive a stuck workflow", "payment", true, "missing", "Continues a workflow that stopped at a failed step.", $"No engine host has a re-drive path from the runtime, and the store does not record an instance's engine id ({I101})."),
        new("reset-activity", "Reset an activity (Temporal)", "payment", true, "missing", "Resets the Temporal workflow to before the failed activity.", $"The store does not record the Temporal workflow id and run id, and no host calls Temporal's reset ({I101})."),
        new("resend-file", "Resend a file to the Central Bank", "payment", true, "missing", "Sends the scheme file again through its exchange.", $"resend is not in the DSL: an exchange declares no resend point ({I101}, I-99)."),
        new("expire-hold", "Expire a hold", "payment", true, "missing", "Ends an open hold as expired before its expiry.", $"The waiting attempt ends a hold only on a decision or its own timeout; it reads no expiry fact ({I101}, I-98)."),
        new("reconcile-posting", "Reconcile an unknown posting with core banking", "payment", false, "missing", "Asks core banking for the fate of a posting whose answer was lost.", $"The core-banking port has no inquiry operation, and its gateway adapter no inquiry call ({I101})."),
        new("pause-intake", "Pause intake for a scheme", "scheme", true, "missing", "Stops new payments of a scheme at the front door.", $"The front door reads no intake switch ({I101})."),
        new("resume-intake", "Resume intake for a scheme", "scheme", false, "missing", "Lets new payments of a scheme in again.", $"The front door reads no intake switch ({I101})."),
        new("drain-worker", "Drain a worker", "worker", true, "missing", "Stops a worker taking new tasks and lets it finish those it has.", $"Workers expose no drain control the runtime can call ({I101})."),
        new("restart-worker", "Restart a worker", "worker", true, "missing", "Restarts a worker process.", $"Workers run under the installation's orchestrator; the runtime has no path to it ({I101})."),
    ];

    readonly Dictionary<string, Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>>> runners = new(StringComparer.Ordinal);

    public void Run(string action, Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> runner) => runners[action] = runner;

    public static ActionSpec Spec(string action) => Catalogue.FirstOrDefault(a => a.Id == action) ?? throw new ServiceRefused(404, ErrorCodes.ActionNotOffered, $"No troubleshooting action {action}");

    /// <summary>A person asks for an action. Not risky: carried out now. Risky: it waits for a ServiceApprover.</summary>
    public async Task<JsonObject> Request(ServiceIdentity who, ActionAsk ask, CancellationToken ct = default)
    {
        var spec = Spec(ask.Action);
        if (!who.Has(ServiceRoles.Engineer)) throw new ServiceRefused(403, ErrorCodes.Forbidden, $"{ask.Action} needs the {ServiceRoles.Engineer} role");
        if (string.IsNullOrWhiteSpace(ask.Reason)) throw new ServiceRefused(400, ErrorCodes.RemarksRequired, $"{ask.Action} needs a reason: who asked and why");
        if (spec.Target != "read-model" && string.IsNullOrWhiteSpace(ask.Target)) throw new ServiceRefused(400, ErrorCodes.ActionNotOffered, $"{ask.Action} needs a {spec.Target}");
        var id = Guid.NewGuid().ToString("N");
        var (payment, kind) = spec.Target == "payment" ? await SubjectOf(ask.Target!, ct) : (null, null);
        await audit.Record("action.requested", who, ask.Reason, new JsonObject
        {
            ["action"] = id, ["name"] = ask.Action, ["target"] = ask.Target, ["parameters"] = Masked.Json(ask.Parameters), ["risky"] = spec.Risky, ["path"] = spec.Path,
        }, payment: payment, paymentKind: kind, ct: ct);
        if (spec.Risky) return new JsonObject { ["action"] = id, ["name"] = ask.Action, ["status"] = "pending-approval", ["needs"] = ServiceRoles.Approver, ["code"] = ErrorCodes.ApproverRequired };
        return await Execute(who, id, ask, spec, payment, kind, ct);
    }

    /// <summary>A ServiceApprover decides a risky action another person asked for.</summary>
    public async Task<JsonObject> Decide(ServiceIdentity who, string actionId, bool approve, string? reason, CancellationToken ct = default)
    {
        if (!who.Has(ServiceRoles.Approver)) throw new ServiceRefused(403, ErrorCodes.Forbidden, $"Approving needs the {ServiceRoles.Approver} role");
        if (string.IsNullOrWhiteSpace(reason)) throw new ServiceRefused(400, ErrorCodes.RemarksRequired, "A decision needs a reason");
        var facts = await Facts(actionId, ct);
        var req = facts.FirstOrDefault(f => f.Code == "action.requested") ?? throw new ServiceRefused(404, ErrorCodes.ActionNotOffered, $"No action {actionId}");
        if (facts.Count > 1) throw new ServiceRefused(409, ErrorCodes.AlreadyDecided, $"Action {actionId} is already {facts[^1].Code}");
        if (req.Actor == who.Actor) throw new ServiceRefused(403, ErrorCodes.MakerApproves, "The person who asked for an action may not approve it");
        var d = (JsonObject)JsonNode.Parse(req.DataJson)!;
        var ask = new ActionAsk((string)d["name"]!, (string?)d["target"], (string?)d["why"], d["parameters"] as JsonObject);
        var spec = Spec(ask.Action);
        var (payment, kind) = spec.Target == "payment" ? await SubjectOf(ask.Target!, ct) : (null, null);
        await audit.Record(approve ? "action.approved" : "action.rejected", who, reason, new JsonObject { ["action"] = actionId, ["name"] = ask.Action }, payment: payment, paymentKind: kind, ct: ct);
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
            catch (ServiceRefused e) { (status, code) = ("refused", e.Code); result = new JsonObject { ["message"] = e.Message }; }
            catch (Exception e) when (e is HttpRequestException or SignatureRejected or SecretUnavailable or InvalidOperationException or IOException)
            { (status, code) = ("refused", ErrorCodes.Of(e)); result = new JsonObject { ["message"] = Masked.Text(e.Message) }; }
        }
        await audit.Record($"action.{status}", who, ask.Reason, new JsonObject { ["action"] = id, ["name"] = ask.Action, ["error"] = code, ["result"] = result.DeepClone() }, payment: payment, paymentKind: kind, ct: ct);
        return new JsonObject { ["action"] = id, ["name"] = ask.Action, ["status"] = status, ["code"] = code, ["runbook"] = code is null ? null : ErrorEntry.RunbookLink(code), ["result"] = result };
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

    /// <summary>Release a hold: the decision the hold waits for (as an operator's would be), on the payment's chain.</summary>
    public static Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> ReleaseHold(StoreOptions store, TimeProvider clock) => async (who, ask, ct) =>
    {
        var subject = ask.Target!;
        var open = await OpenHold(store, subject, ct) ?? throw new ServiceRefused(409, ErrorCodes.HoldClosed, $"{subject} has no open hold");
        var decision = open.kind == "repair" ? "released" : "approved";
        await using var db = StoreDbContext.Open(store);
        var kind = await db.Bulks.AsNoTracking().AnyAsync(b => b.Id == subject, ct) ? "bulk" : "payment";
        await new FactLog(db, clock).Append(subject, kind, new NewFact("hold", "decided", new JsonObject { ["hold"] = open.id, ["decision"] = decision, ["by"] = who.Actor, ["via"] = "service-console" }, Actor: who.Actor), ct);
        return new JsonObject { ["hold"] = open.id, ["decision"] = decision };
    };

    /// <summary>Re-screen: the signal on the outbox for the engine's host (Issue I-99).</summary>
    public static Func<ServiceIdentity, ActionAsk, CancellationToken, Task<JsonObject>> ReScreen(StoreOptions store, IEngineSignals signals) => async (who, ask, ct) =>
    {
        await using var db = StoreDbContext.Open(store);
        var tenant = await db.Bulks.AsNoTracking().Where(b => b.Id == ask.Target).Select(b => b.Tenant).FirstOrDefaultAsync(ct)
            ?? await db.Payments.AsNoTracking().Where(p => p.Id == ask.Target).Select(p => p.Tenant).FirstOrDefaultAsync(ct) ?? "";
        if (!who.Sees(tenant)) throw new ServiceRefused(403, ErrorCodes.Forbidden, $"No tenant {tenant} for {who.DisplayName}");
        var text = await signals.Send(tenant, ask.Target!, null, "re-screen", Guid.NewGuid().ToString("N"), ct);
        return new JsonObject { ["signal"] = "re-screen", ["result"] = text, ["delivered"] = false, ["missing"] = "No engine host reads the engine-signal topic yet (Issue I-99)" };
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
