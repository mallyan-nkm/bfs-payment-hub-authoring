using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Security;

namespace TransactHub.Ports;

// The platform's production adapters (R-09-90, owner's direction of 23 November 2026): sanctions screening (p.port.sanctions.v1),
// fraud (p.port.fraud.v1), the channel's notices by e-mail and SMS (p.port.channel.v1), the business calendar
// (uae.p.port.calendar.v1), the participant directory (uae.p.port.participant-directory.v1) and the outward facts
// (p.port.facts-out.v1). Each is chosen by the bank's binding, effective-dated like every other: `simulator` for tests and the
// example bank, the production adapters for a bank. Settings are plain values; a credential is only ever a `secretRef`, resolved
// when the call is made and never kept in a setting, a log or a fact. No adapter here names a bank or a vendor.

/// <summary>A call to a bank's REST service: the binding's base URL and path, its credential by reference, its timeout.</summary>
static class Rest
{
    public static async Task<HttpResponseMessage> Send(HttpClient http, ISecretResolver secrets, PortBinding b, HttpMethod method, string pathKey, string defaultPath, JsonObject? body, CancellationToken ct, string? query = null)
    {
        var uri = new Uri(new Uri(b.Required("baseUrl")), b.Setting(pathKey, defaultPath) + (query is null ? "" : "?" + query));
        var m = new HttpRequestMessage(method, uri);
        if (body is not null) m.Content = JsonContent.Create(body);
        if (b.Setting("secretRef", "") is { Length: > 0 } r)
        {
            var token = (await secrets.Resolve(r, ct)).Reveal();
            if (b.Setting("credentialHeader", "") is { Length: > 0 } h) m.Headers.Add(h, token);
            else m.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(double.Parse(b.Setting("timeoutSeconds", "10"), CultureInfo.InvariantCulture)));
        var res = await http.SendAsync(m, timeout.Token);
        // 5xx and 429 are the service's own trouble: the caller retries (TransientError); any other failure is a refusal.
        if ((int)res.StatusCode >= 500 || res.StatusCode == HttpStatusCode.TooManyRequests) throw new HttpRequestException($"{b.Port}: the service answered {(int)res.StatusCode}");
        return res;
    }

    public static async Task<JsonObject> Json(HttpResponseMessage res, CancellationToken ct)
    {
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonObject>(ct) ?? new JsonObject();
    }
}

// ------------------------------------------------------------------------------------------------ screening

/// <summary>What a screening engine said: accepted (clear), rejected (a hit or a suspect), or pending; its reason code and its reference.</summary>
public sealed record ScreeningAnswer(string Status, string? ReasonCode, string? Reference);

/// <summary>A sanctions or fraud screening engine. The request carries the payment's ids and the parties the activity reads.</summary>
public interface IScreening
{
    string Adapter { get; }
    Task<ScreeningAnswer> Screen(JsonObject request, CancellationToken ct = default);
}

public static class ScreeningAdapters
{
    public const string Sanctions = "p.port.sanctions.v1";
    public const string Fraud = "p.port.fraud.v1";

    public static IScreening Create(PortBinding b, HttpClient? http = null, ISecretResolver? secrets = null) => b.Adapter switch
    {
        SimulatedScreening.Name => new SimulatedScreening(b),
        RestScreening.Name => new RestScreening(b, http ?? new HttpClient(), secrets ?? SecretResolver.FromEnvironment()),
        _ => throw new PortBindingException($"No screening adapter is called '{b.Adapter}' (binding for {b.Port}, from {b.ValidFrom:O})"),
    };

    /// <summary>An engine's words for its answer, as the platform's outcomes take them.</summary>
    public static string Normalise(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "clear" or "accepted" or "pass" or "no-hit" or "nohit" => "accepted",
        "hit" or "suspect" or "rejected" or "block" or "blocked" or "match" => "rejected",
        "pending" or "review" or "in-review" or "potential" => "pending",
        _ => "pending",
    };
}

/// <summary>Settings: <c>answer</c> (accepted, rejected or pending; default accepted), <c>reasonCode</c> for a refusal.</summary>
public sealed class SimulatedScreening(PortBinding b) : IScreening
{
    public const string Name = "simulator";
    public string Adapter => Name;
    public Task<ScreeningAnswer> Screen(JsonObject request, CancellationToken ct = default)
    {
        var s = ScreeningAdapters.Normalise(b.Setting("answer", "accepted"));
        return Task.FromResult(new ScreeningAnswer(s, s == "rejected" ? b.Setting("reasonCode", b.Port == ScreeningAdapters.Fraud ? "FRAD" : "RR04") : null, $"sim-{(string?)request["paymentId"]}"));
    }
}

/// <summary>
/// The bank's screening engine behind a REST service: <c>POST {baseUrl}{screenPath}</c> (default <c>/screen</c>) with the request;
/// the reply's <c>result</c> or <c>status</c> (clear, hit, suspect, pending), <c>reasonCode</c> and <c>reference</c>. The credential
/// by <c>secretRef</c> as a bearer token, or in the header <c>credentialHeader</c> names. 5xx and timeouts are transient.
/// </summary>
public sealed class RestScreening(PortBinding b, HttpClient http, ISecretResolver secrets) : IScreening
{
    public const string Name = "gateway-rest";
    public string Adapter => Name;
    public async Task<ScreeningAnswer> Screen(JsonObject request, CancellationToken ct = default)
    {
        using var res = await Rest.Send(http, secrets, b, HttpMethod.Post, "screenPath", "/screen", request, ct);
        var j = await Rest.Json(res, ct);
        return new ScreeningAnswer(ScreeningAdapters.Normalise((string?)j["result"] ?? (string?)j["status"]), (string?)j["reasonCode"], (string?)j["reference"]);
    }
}

// ------------------------------------------------------------------------------------------------ notices

/// <summary>A notice to the originating channel: the payment's ids and status, never a party's name or account.</summary>
public sealed record Notice(string PaymentId, string Status, string Subject, string Text);

public interface INotices
{
    string Adapter { get; }
    /// <summary>Sends the notice; answers the transport's reference.</summary>
    Task<string> Send(Notice n, CancellationToken ct = default);
}

public static class NoticeAdapters
{
    public const string Port = "p.port.channel.v1";

    public static INotices Create(PortBinding b, HttpClient? http = null, ISecretResolver? secrets = null) => b.Adapter switch
    {
        SimulatedNotices.Name => new SimulatedNotices(b),
        EmailNotices.Name => new EmailNotices(b, secrets ?? SecretResolver.FromEnvironment()),
        SmsNotices.Name => new SmsNotices(b, http ?? new HttpClient(), secrets ?? SecretResolver.FromEnvironment()),
        _ => throw new PortBindingException($"No notice adapter is called '{b.Adapter}' (binding for {b.Port}, from {b.ValidFrom:O})"),
    };
}

/// <summary>Keeps what it was asked to send, for tests.</summary>
public sealed class SimulatedNotices(PortBinding b) : INotices
{
    public const string Name = "simulator";
    public string Adapter => Name;
    public static readonly ConcurrentQueue<(string Tenant, Notice Notice)> Sent = new();
    public Task<string> Send(Notice n, CancellationToken ct = default)
    {
        Sent.Enqueue((b.Tenant, n));
        return Task.FromResult($"sim-{n.PaymentId}");
    }
}

/// <summary>
/// E-mail through the bank's SMTP relay: settings <c>host</c>, <c>port</c> (default 587), <c>from</c>, <c>to</c> (the channel's
/// mailbox, a comma list), <c>tls</c> (default true); the login by <c>secretRef</c> as <c>user:password</c>, else none.
/// </summary>
public sealed class EmailNotices(PortBinding b, ISecretResolver secrets) : INotices
{
    public const string Name = "email";
    public string Adapter => Name;
    public async Task<string> Send(Notice n, CancellationToken ct = default)
    {
#pragma warning disable SYSLIB0006, CS0618 // SmtpClient: the platform's own client is enough for a relay inside the bank
        using var smtp = new System.Net.Mail.SmtpClient(b.Required("host"), int.Parse(b.Setting("port", "587"), CultureInfo.InvariantCulture))
        {
            EnableSsl = bool.Parse(b.Setting("tls", "true")),
            DeliveryMethod = System.Net.Mail.SmtpDeliveryMethod.Network,
            Timeout = (int)(double.Parse(b.Setting("timeoutSeconds", "10"), CultureInfo.InvariantCulture) * 1000),
        };
        if (b.Setting("secretRef", "") is { Length: > 0 } r)
        {
            var login = (await secrets.Resolve(r, ct)).Reveal();
            var i = login.IndexOf(':');
            if (i <= 0) throw new PortBindingException($"The email binding of {b.Port} for {b.Tenant}: its secretRef holds no user:password");
            smtp.Credentials = new NetworkCredential(login[..i], login[(i + 1)..]);
        }
        var id = $"<{Guid.NewGuid():N}@transacthub>";
        using var m = new System.Net.Mail.MailMessage { From = new System.Net.Mail.MailAddress(b.Required("from")), Subject = n.Subject, Body = n.Text };
        foreach (var to in b.Required("to").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) m.To.Add(to);
        m.Headers.Add("Message-ID", id);
        await smtp.SendMailAsync(m, ct);
#pragma warning restore SYSLIB0006, CS0618
        return id;
    }
}

/// <summary>
/// SMS through the bank's SMS gateway: <c>POST {baseUrl}{sendPath}</c> (default <c>/messages</c>) with <c>{ to, from, text,
/// reference }</c>, <c>to</c> the channel's number from the setting; the reply's <c>id</c> is the reference. Credential by <c>secretRef</c>.
/// </summary>
public sealed class SmsNotices(PortBinding b, HttpClient http, ISecretResolver secrets) : INotices
{
    public const string Name = "sms";
    public string Adapter => Name;
    public async Task<string> Send(Notice n, CancellationToken ct = default)
    {
        var body = new JsonObject { ["to"] = b.Required("to"), ["from"] = b.Setting("from", "TransactHub"), ["text"] = n.Text, ["reference"] = n.PaymentId };
        using var res = await Rest.Send(http, secrets, b, HttpMethod.Post, "sendPath", "/messages", body, ct);
        var j = await Rest.Json(res, ct);
        return (string?)j["id"] ?? (string?)j["reference"] ?? n.PaymentId;
    }
}

// ------------------------------------------------------------------------------------------------ reference reads

/// <summary>How a host reads a reference table its configuration installed (R-09-75): the rows of the edition in force, with the table's name.</summary>
public delegate Task<(string? Table, JsonArray? Rows)> ReferenceReader(string tenant, string domain, DateTimeOffset at, CancellationToken ct);

// ------------------------------------------------------------------------------------------------ business calendar

/// <summary>What the calendar says of a day: whether it is a business day, and the business date it counts as (the next one if not).</summary>
public sealed record BusinessDay(string Date, bool IsBusinessDay, string BusinessDate, string Source);

public interface IBusinessCalendarService
{
    string Adapter { get; }
    Task<BusinessDay> Of(DateTimeOffset at, CancellationToken ct = default);
}

public static class CalendarAdapters
{
    public const string Port = "uae.p.port.calendar.v1";
    public const string Domain = "business-calendar";
    static readonly string[] Days = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    public static IBusinessCalendarService Create(PortBinding b, ReferenceReader? reader = null, HttpClient? http = null, ISecretResolver? secrets = null) => b.Adapter switch
    {
        ReferenceCalendar.Name => new ReferenceCalendar(b, reader ?? throw new PortBindingException($"{b.Port} for {b.Tenant} is bound to the reference table, but this host has no store")),
        SimulatedCalendar.Name => new SimulatedCalendar(b),
        RestCalendar.Name => new RestCalendar(b, http ?? new HttpClient(), secrets ?? SecretResolver.FromEnvironment()),
        _ => throw new PortBindingException($"No business calendar adapter is called '{b.Adapter}' (binding for {b.Port}, from {b.ValidFrom:O})"),
    };

    /// <summary>The weekend days and the holidays of a table's rows (<c>day</c>: a weekday name or an ISO date).</summary>
    public static (HashSet<int> Weekend, HashSet<string> Holidays) Of(JsonArray? rows)
    {
        var weekend = new HashSet<int>();
        var holidays = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows ?? [])
        {
            var d = (string?)r?["day"] ?? "";
            var w = Array.IndexOf(Days, d);
            if (w >= 0) weekend.Add(w);
            else if (DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) holidays.Add(d);
        }
        return (weekend, holidays);
    }

    /// <summary>The days a bank's own calendar (domain bank-calendar) says it is closed: rows of kind <c>closed</c> with an ISO date.</summary>
    public static HashSet<string> ClosedDays(JsonArray? rows) =>
        (rows ?? []).OfType<JsonObject>().Where(r => (string?)r["kind"] == "closed").Select(r => (string?)r["day"] ?? "")
            .Where(d => DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)).ToHashSet(StringComparer.Ordinal);

    public static BusinessDay Count(DateTimeOffset at, HashSet<int> weekend, HashSet<string> holidays, string source)
    {
        bool Business(DateOnly d) => !weekend.Contains((int)d.DayOfWeek) && !holidays.Contains(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var day = DateOnly.FromDateTime(at.UtcDateTime);
        var next = day;
        for (var i = 0; i < 366 && !Business(next); i++) next = next.AddDays(1);
        return new BusinessDay(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Business(day), next.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), source);
    }
}

/// <summary>
/// The bank's business calendar reference table (domain business-calendar), the edition in force on the day, and the bank's own
/// closing days (domain bank-calendar, setting <c>bankDomain</c>, R-11-52): a day of kind <c>closed</c> is no business day either
/// (R-09-102). A half day is a business day; it moves the cut-off, not the business date (p.decide.cut-off.v1).
/// </summary>
public sealed class ReferenceCalendar(PortBinding b, ReferenceReader reader) : IBusinessCalendarService
{
    public const string Name = "reference";
    public const string BankDomain = "bank-calendar";
    public string Adapter => Name;
    public async Task<BusinessDay> Of(DateTimeOffset at, CancellationToken ct = default)
    {
        var (table, rows) = await reader(b.Tenant, b.Setting("domain", CalendarAdapters.Domain), at, ct);
        if (rows is null) throw new PortBindingException($"{b.Tenant} has no reference table of domain {b.Setting("domain", CalendarAdapters.Domain)} in force on {at:yyyy-MM-dd}");
        var (w, h) = CalendarAdapters.Of(rows);
        var source = table ?? CalendarAdapters.Domain;
        var (bankTable, bankRows) = await reader(b.Tenant, b.Setting("bankDomain", BankDomain), at, ct);
        var closed = CalendarAdapters.ClosedDays(bankRows);
        if (closed.Count > 0)
        {
            h.UnionWith(closed);
            source = $"{source}+{bankTable ?? BankDomain}";
        }
        return CalendarAdapters.Count(at, w, h, source);
    }
}

/// <summary>Settings: <c>weekend</c> (comma list of day numbers, default 0,6), <c>holidays</c> (comma list of ISO dates).</summary>
public sealed class SimulatedCalendar(PortBinding b) : IBusinessCalendarService
{
    public const string Name = "simulator";
    public string Adapter => Name;
    public Task<BusinessDay> Of(DateTimeOffset at, CancellationToken ct = default)
    {
        var w = b.Setting("weekend", "0,6").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToHashSet();
        var h = b.Setting("holidays", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(CalendarAdapters.Count(at, w, h, Name));
    }
}

/// <summary>A calendar service: <c>GET {baseUrl}{datePath}?date=YYYY-MM-DD</c> (default <c>/business-date</c>), reply <c>{ businessDay, businessDate }</c>.</summary>
public sealed class RestCalendar(PortBinding b, HttpClient http, ISecretResolver secrets) : IBusinessCalendarService
{
    public const string Name = "gateway-rest";
    public string Adapter => Name;
    public async Task<BusinessDay> Of(DateTimeOffset at, CancellationToken ct = default)
    {
        var date = at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var res = await Rest.Send(http, secrets, b, HttpMethod.Get, "datePath", "/business-date", null, ct, $"date={date}");
        var j = await Rest.Json(res, ct);
        return new BusinessDay(date, (bool?)j["businessDay"] ?? false, (string?)j["businessDate"] ?? date, Name);
    }
}

// ------------------------------------------------------------------------------------------------ participant directory

/// <summary>A participant of the scheme by its routing code: whether it is known, its name and BIC as the directory holds them.</summary>
public sealed record Participant(string RoutingCode, bool Found, string? Name, string? Bic, string Source);

public interface IParticipantDirectory
{
    string Adapter { get; }
    Task<Participant> Lookup(string routingCode, DateTimeOffset at, CancellationToken ct = default);
}

public static class ParticipantDirectoryAdapters
{
    public const string Port = "uae.p.port.participant-directory.v1";
    public const string Domain = "participant-directory";

    public static IParticipantDirectory Create(PortBinding b, ReferenceReader? reader = null, HttpClient? http = null, ISecretResolver? secrets = null) => b.Adapter switch
    {
        ReferenceDirectory.Name => new ReferenceDirectory(b, reader ?? throw new PortBindingException($"{b.Port} for {b.Tenant} is bound to the reference table, but this host has no store")),
        SimulatedDirectory.Name => new SimulatedDirectory(b),
        RestDirectory.Name => new RestDirectory(b, http ?? new HttpClient(), secrets ?? SecretResolver.FromEnvironment()),
        _ => throw new PortBindingException($"No participant directory adapter is called '{b.Adapter}' (binding for {b.Port}, from {b.ValidFrom:O})"),
    };
}

/// <summary>The directory as a reference table (domain participant-directory, R-01-25): the row whose <c>routingCode</c> is the code.</summary>
public sealed class ReferenceDirectory(PortBinding b, ReferenceReader reader) : IParticipantDirectory
{
    public const string Name = "reference";
    public string Adapter => Name;
    public async Task<Participant> Lookup(string routingCode, DateTimeOffset at, CancellationToken ct = default)
    {
        var (table, rows) = await reader(b.Tenant, b.Setting("domain", ParticipantDirectoryAdapters.Domain), at, ct);
        var row = (rows ?? []).OfType<JsonObject>().FirstOrDefault(r => (string?)r[b.Setting("keyColumn", "routingCode")] == routingCode);
        return new Participant(routingCode, row is not null, (string?)row?["name"], (string?)row?["bic"], table ?? ParticipantDirectoryAdapters.Domain);
    }
}

/// <summary>Settings: <c>known</c>, a comma list of <c>code=name</c>; any other code is unknown.</summary>
public sealed class SimulatedDirectory(PortBinding b) : IParticipantDirectory
{
    public const string Name = "simulator";
    public string Adapter => Name;
    public Task<Participant> Lookup(string routingCode, DateTimeOffset at, CancellationToken ct = default)
    {
        foreach (var e in b.Setting("known", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (e.Split('=', 2) is [var code, var name] && code == routingCode) return Task.FromResult(new Participant(routingCode, true, name, null, Name));
        return Task.FromResult(new Participant(routingCode, false, null, null, Name));
    }
}

/// <summary>A directory service: <c>GET {baseUrl}{lookupPath}/{routingCode}</c> (default <c>/participants</c>), reply <c>{ name, bic }</c>; 404 unknown.</summary>
public sealed class RestDirectory(PortBinding b, HttpClient http, ISecretResolver secrets) : IParticipantDirectory
{
    public const string Name = "gateway-rest";
    public string Adapter => Name;
    public async Task<Participant> Lookup(string routingCode, DateTimeOffset at, CancellationToken ct = default)
    {
        var b2 = b with { Settings = new Dictionary<string, string>(b.Settings) { ["lookupPath"] = $"{b.Setting("lookupPath", "/participants").TrimEnd('/')}/{Uri.EscapeDataString(routingCode)}" } };
        using var res = await Rest.Send(http, secrets, b2, HttpMethod.Get, "lookupPath", "/participants", null, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return new Participant(routingCode, false, null, null, Name);
        var j = await Rest.Json(res, ct);
        return new Participant(routingCode, true, (string?)j["name"], (string?)j["bic"], Name);
    }
}

// ------------------------------------------------------------------------------------------------ facts out

public interface IFactsOut
{
    string Adapter { get; }
    /// <summary>Publishes one fact, once by its key (a repeat with the same key is the same fact); answers the reference.</summary>
    Task<string> Publish(string key, JsonObject fact, CancellationToken ct = default);
}

public static class FactsOutAdapters
{
    public const string Port = "p.port.facts-out.v1";

    public static IFactsOut Create(PortBinding b, HttpClient? http = null, ISecretResolver? secrets = null) => b.Adapter switch
    {
        SimulatedFactsOut.Name => new SimulatedFactsOut(b),
        WebhookFactsOut.Name => new WebhookFactsOut(b, http ?? new HttpClient(), secrets ?? SecretResolver.FromEnvironment()),
        _ => throw new PortBindingException($"No facts-out adapter is called '{b.Adapter}' (binding for {b.Port}, from {b.ValidFrom:O})"),
    };

    /// <summary>The signature a receiver checks: HMAC-SHA256 of the body with the shared key, as hex.</summary>
    public static string Sign(byte[] body, string key) => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), body));
}

public sealed class SimulatedFactsOut(PortBinding b) : IFactsOut
{
    public const string Name = "simulator";
    public string Adapter => Name;
    public static readonly ConcurrentDictionary<string, JsonObject> Published = new();
    public Task<string> Publish(string key, JsonObject fact, CancellationToken ct = default)
    {
        Published.TryAdd($"{b.Tenant}|{key}", (JsonObject)fact.DeepClone());
        return Task.FromResult($"sim-{key}");
    }
}

/// <summary>
/// The bank's event endpoint: <c>POST {baseUrl}{publishPath}</c> (default <c>/facts</c>) with the fact, the header
/// <c>Idempotency-Key</c> (the fact's key) and <c>X-TH-Signature</c>, HMAC-SHA256 of the body with the key <c>signingKeyRef</c>
/// names (by reference); the credential by <c>secretRef</c>. A 409 is a fact the endpoint already has.
/// </summary>
public sealed class WebhookFactsOut(PortBinding b, HttpClient http, ISecretResolver secrets) : IFactsOut
{
    public const string Name = "webhook";
    public string Adapter => Name;
    public async Task<string> Publish(string key, JsonObject fact, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(fact.ToJsonString());
        using var m = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(b.Required("baseUrl")), b.Setting("publishPath", "/facts")))
        {
            Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/json") } },
        };
        m.Headers.Add("Idempotency-Key", key);
        if (b.Setting("signingKeyRef", "") is { Length: > 0 } sk) m.Headers.Add("X-TH-Signature", FactsOutAdapters.Sign(bytes, (await secrets.Resolve(sk, ct)).Reveal()));
        if (b.Setting("secretRef", "") is { Length: > 0 } r) m.Headers.Authorization = new("Bearer", (await secrets.Resolve(r, ct)).Reveal());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(double.Parse(b.Setting("timeoutSeconds", "10"), CultureInfo.InvariantCulture)));
        using var res = await http.SendAsync(m, timeout.Token);
        if ((int)res.StatusCode >= 500 || res.StatusCode == HttpStatusCode.TooManyRequests) throw new HttpRequestException($"{b.Port}: the endpoint answered {(int)res.StatusCode}");
        if (res.StatusCode == HttpStatusCode.Conflict) return key;
        res.EnsureSuccessStatusCode();
        var text = await res.Content.ReadAsStringAsync(ct);
        return text.Length > 0 && JsonNode.Parse(text) is JsonObject j && (string?)j["id"] is string id ? id : key;
    }
}
