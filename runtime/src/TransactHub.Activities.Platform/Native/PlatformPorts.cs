using System.Text.Json.Nodes;
using TransactHub.Ports;

namespace TransactHub.Activities.Native;

/// <summary>
/// The platform's activities on the runtime's production adapters (R-09-90): when a deployment gives a port to the runtime
/// (R-08-15), its builtin activity runs here, on the bank's binding of the port in force at the attempt. Each writes what the
/// studio's reference behaviour writes (<c>outcome</c>, <c>result</c>, the party's <c>status</c> and <c>reasonCode</c>) and the
/// adapter's reference; nothing the party said beyond that reaches the workflow's state. A party that does not answer, or answers
/// 5xx, is a <c>TransientError</c>, which the activity's policy retries.
/// </summary>
public static class PlatformPortActivities
{
    public static INativeActivity[] All(PortBindings bindings, ReferenceReader? reader = null, HttpClient? http = null) =>
    [
        new Screen("p.screen.sanctions.v1", "facts.screening.sanctions", "hit", "pending", new BoundAdapters<IScreening>(bindings, ScreeningAdapters.Sanctions, b => ScreeningAdapters.Create(b, http))),
        new Screen("p.screen.fraud.v1", "facts.screening.fraud", "suspect", "suspect", new BoundAdapters<IScreening>(bindings, ScreeningAdapters.Fraud, b => ScreeningAdapters.Create(b, http))),
        new NotifyChannel(new BoundAdapters<INotices>(bindings, NoticeAdapters.Port, b => NoticeAdapters.Create(b, http))),
        new PublishFact(new BoundAdapters<IFactsOut>(bindings, FactsOutAdapters.Port, b => FactsOutAdapters.Create(b, http))),
        new EnrichBusinessDate(new BoundAdapters<IBusinessCalendarService>(bindings, CalendarAdapters.Port, b => CalendarAdapters.Create(b, reader, http))),
        new EnrichParticipant(new BoundAdapters<IParticipantDirectory>(bindings, ParticipantDirectoryAdapters.Port, b => ParticipantDirectoryAdapters.Create(b, reader, http))),
    ];

    internal static T Call<T>(string port, Func<Task<T>> call)
    {
        try { return call().GetAwaiter().GetResult(); }
        // An open breaker sent nothing (R-09-155): its own code, retried as a party that does not answer (Issue I-259).
        catch (CircuitOpenException e) { throw new NativeFailure(CircuitBreakers.OpenCode, e.Message, true); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or TimeoutException or System.Net.Mail.SmtpException)
        { throw new NativeFailure("TransientError", $"{port} did not answer: {e.GetType().Name}", true); }
        catch (PortBindingException e) { throw new NativeFailure("PortNotBound", e.Message, false); }
    }

    internal static JsonObject Fact(string outcome, string? status, string? reasonCode, string? reference)
    {
        var f = new JsonObject { ["outcome"] = outcome, ["result"] = outcome };
        if (status is not null) f["status"] = status;
        if (reasonCode is not null) f["reasonCode"] = reasonCode;
        if (reference is not null) f["reference"] = reference;
        return f;
    }

    internal static JsonObject Ids(JsonObject view)
    {
        var h = view["header"] as JsonObject;
        var o = new JsonObject();
        foreach (var k in new[] { "paymentId", "endToEndId", "uetr", "fileId", "scheme", "direction" })
            if ((string?)h?[k] is string v) o[k] = v;
        return o;
    }
}

/// <summary><c>p.screen.sanctions.v1</c> and <c>p.screen.fraud.v1</c>: the parties the activity reads, sent to the bank's engine.</summary>
public sealed class Screen(string name, string fact, string negative, string pending, BoundAdapters<IScreening> engine) : INativeActivity
{
    public string Name => name;

    public NativeResult Run(NativeAttempt a)
    {
        var (s, _) = engine.For(a.Tenant, a.Now);
        var request = PlatformPortActivities.Ids(a.View);
        request["kind"] = name == "p.screen.fraud.v1" ? "fraud" : "sanctions";
        // The parties go to the bank's own engine, which must see them to screen; they are not written anywhere here.
        request["payment"] = a.View["payment"]?.DeepClone();
        var answer = PlatformPortActivities.Call(engine.Port, () => s.Screen(request));
        var outcome = answer.Status switch { "accepted" => "clear", "rejected" => negative, _ => pending };
        return new NativeResult(outcome, outcome == "clear" ? null : answer.ReasonCode,
            new JsonObject { [fact] = PlatformPortActivities.Fact(outcome, answer.Status, outcome == "clear" ? null : answer.ReasonCode, answer.Reference) });
    }
}

/// <summary><c>p.notify.channel.v1</c>: the payment's status told to the channel by e-mail or SMS, as the bank binds it; its ids only.</summary>
public sealed class NotifyChannel(BoundAdapters<INotices> notices) : INativeActivity
{
    public string Name => "p.notify.channel.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var (n, b) = notices.For(a.Tenant, a.Now);
        var id = NativeContext.S(a.View, "header.paymentId");
        var status = NativeContext.S(a.View, "status.code") is { Length: > 0 } c ? c : "UNKNOWN";
        var notice = new Notice(id, status, $"Payment {id}: {status}", $"Payment {id} is now {status}.");
        var reference = PlatformPortActivities.Call(notices.Port, () => n.Send(notice));
        var fact = PlatformPortActivities.Fact("notified", "accepted", null, reference);
        fact["channel"] = b.Adapter;
        return new NativeResult("notified", null, new JsonObject { ["facts.notified"] = fact });
    }
}

/// <summary><c>p.notify.publish-fact.v1</c>: the payment's status published outward, once per payment and status (its key).</summary>
public sealed class PublishFact(BoundAdapters<IFactsOut> facts) : INativeActivity
{
    public string Name => "p.notify.publish-fact.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var (f, _) = facts.For(a.Tenant, a.Now);
        var body = PlatformPortActivities.Ids(a.View);
        body["status"] = a.View["status"]?.DeepClone();
        body["at"] = a.Now.ToString("O");
        var key = $"{NativeContext.S(a.View, "header.paymentId")}:{NativeContext.S(a.View, "status.code")}";
        var reference = PlatformPortActivities.Call(facts.Port, () => f.Publish(key, body));
        return new NativeResult("notified", null, new JsonObject { ["facts.published"] = PlatformPortActivities.Fact("notified", "accepted", null, reference) });
    }
}

/// <summary><c>uae.p.enrich.business-date.v1</c>: the business date the payment's receipt counts as, on the bank's calendar.</summary>
public sealed class EnrichBusinessDate(BoundAdapters<IBusinessCalendarService> calendar) : INativeActivity
{
    public string Name => "uae.p.enrich.business-date.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var (c, _) = calendar.For(a.Tenant, a.Now);
        var at = DateTimeOffset.TryParse(NativeContext.S(a.View, "header.receivedAt"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var r) ? r : a.Now;
        var day = PlatformPortActivities.Call(calendar.Port, () => c.Of(at));
        return new NativeResult("set", null, new JsonObject { ["header.businessDate"] = day.BusinessDate });
    }
}

/// <summary><c>uae.p.enrich.participant.v1</c>: the debtor's and the creditor's agent looked up by routing code in the directory.</summary>
public sealed class EnrichParticipant(BoundAdapters<IParticipantDirectory> directory) : INativeActivity
{
    public string Name => "uae.p.enrich.participant.v1";

    public NativeResult Run(NativeAttempt a)
    {
        var (d, _) = directory.For(a.Tenant, a.Now);
        var fact = new JsonObject();
        var all = true;
        foreach (var agent in new[] { "debtorAgent", "creditorAgent" })
        {
            var code = NativeContext.S(a.View, $"payment.{agent}.clearingMemberId") is { Length: > 0 } x ? x : NativeContext.S(a.View, $"payment.{agent}.memberId");
            if (code.Length == 0) continue;
            var p = PlatformPortActivities.Call(directory.Port, () => d.Lookup(code, a.Now));
            all &= p.Found;
            var o = new JsonObject { ["routingCode"] = code, ["found"] = p.Found };
            if (p.Name is not null) o["name"] = p.Name;
            if (p.Bic is not null) o["bic"] = p.Bic;
            fact[agent] = o;
        }
        var outcome = all ? "found" : "unknown";
        fact["outcome"] = outcome;
        fact["result"] = outcome;
        return new NativeResult(outcome, all ? null : "RC01", new JsonObject { ["facts.participants"] = fact });
    }
}
