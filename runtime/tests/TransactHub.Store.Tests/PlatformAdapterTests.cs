using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Ports;
using TransactHub.Security;
using Xunit;

namespace TransactHub.Store.Tests;

/// <summary>
/// The platform's production adapters (R-09-90): sanctions and fraud screening, the channel's notices by e-mail and SMS, the
/// business calendar, the participant directory and the outward facts, each proved against a local stand-in of its counterpart
/// (an HTTP service or an SMTP relay on a loopback port, made at test time), with its credential by reference only; and the banks'
/// bindings of them load, with no host and no key in them.
/// </summary>
public sealed class PlatformAdapterTests
{
    static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-27T06:00:00Z");
    static readonly SecretResolver Secrets = new([new EnvSecretProvider(k => k switch { "TH_TEST_TOKEN" => "token-for-test", "TH_TEST_SMTP" => "relay-user:relay-pass", "TH_TEST_SIGN" => "signing-key-for-test", _ => null })]);

    /// <summary>An HTTP stand-in on a loopback port: each request recorded, answered by the test's function.</summary>
    sealed class StandIn : IDisposable
    {
        readonly HttpListener listener = new();
        public readonly ConcurrentQueue<(string Method, string Path, string Query, string Body, NameValueCollectionCopy Headers)> Calls = new();
        public string Url { get; }

        public StandIn(Func<string, string, string, (int, string)> answer)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            Url = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(Url);
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await listener.GetContextAsync(); } catch { return; }
                    var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
                    Calls.Enqueue((c.Request.HttpMethod, c.Request.Url!.AbsolutePath, c.Request.Url.Query, body, new NameValueCollectionCopy(c.Request.Headers)));
                    var (status, text) = answer(c.Request.HttpMethod, c.Request.Url.AbsolutePath, body);
                    c.Response.StatusCode = status;
                    var bytes = Encoding.UTF8.GetBytes(text);
                    c.Response.ContentType = "application/json";
                    await c.Response.OutputStream.WriteAsync(bytes);
                    c.Response.Close();
                }
            });
        }

        public void Dispose() { try { listener.Close(); } catch { } }
    }

    sealed class NameValueCollectionCopy(System.Collections.Specialized.NameValueCollection h)
    {
        readonly Dictionary<string, string?> d = h.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => h[k], StringComparer.OrdinalIgnoreCase);
        public string? this[string k] => d.GetValueOrDefault(k);
    }

    static PortBinding Bind(string port, string adapter, Dictionary<string, string> settings) => new("test-bank", port, adapter, From, settings);

    [Fact]
    public async Task Screening_on_the_banks_engine_clear_hit_pending_and_its_trouble_transient()
    {
        using var engine = new StandIn((_, _, body) => (string?)JsonNode.Parse(body)!["paymentId"] switch
        {
            "P-HIT" => (200, """{ "result": "HIT", "reasonCode": "RR04", "reference": "case-7" }"""),
            "P-PEND" => (200, """{ "status": "review" }"""),
            "P-DOWN" => (503, "{}"),
            _ => (200, """{ "result": "clear", "reference": "case-1" }"""),
        });
        var b = Bind(ScreeningAdapters.Sanctions, RestScreening.Name, new() { ["baseUrl"] = engine.Url, ["screenPath"] = "/v1/screen", ["secretRef"] = "env:TH_TEST_TOKEN" });
        var s = ScreeningAdapters.Create(b, new HttpClient(), Secrets);
        Assert.Equal(new ScreeningAnswer("accepted", null, "case-1"), await s.Screen(new JsonObject { ["paymentId"] = "P-1" }));
        Assert.Equal(new ScreeningAnswer("rejected", "RR04", "case-7"), await s.Screen(new JsonObject { ["paymentId"] = "P-HIT" }));
        Assert.Equal("pending", (await s.Screen(new JsonObject { ["paymentId"] = "P-PEND" })).Status);
        await Assert.ThrowsAsync<HttpRequestException>(() => s.Screen(new JsonObject { ["paymentId"] = "P-DOWN" }));
        var call = engine.Calls.First();
        Assert.Equal(("POST", "/v1/screen"), (call.Method, call.Path));
        Assert.Equal("Bearer token-for-test", call.Headers["Authorization"]);

        // The activity on the runtime: the outcome and the fact the reference behaviour writes; a 503 is a TransientError.
        var bindings = new PortBindings();
        bindings.Add($"""
            kind: port-bindings
            tenant: test-bank
            bindings:
              - port: p.port.sanctions.v1
                adapter: gateway-rest
                validFrom: 2026-11-01T00:00:00Z
                settings: {"{"} baseUrl: "{engine.Url}", secretRef: "env:TH_TEST_TOKEN" {"}"}
              - port: p.port.fraud.v1
                adapter: simulator
                validFrom: 2026-11-01T00:00:00Z
                settings: {"{"} answer: suspect {"}"}
            """);
        var acts = PlatformPortActivities.All(bindings, null, new HttpClient()).ToDictionary(a => a.Name);
        Environment.SetEnvironmentVariable("TH_TEST_TOKEN", "token-for-test");
        try
        {
            NativeResult Run(string act, string id) => acts[act].Run(new NativeAttempt(act, "1.0.0", new JsonObject { ["header"] = new JsonObject { ["paymentId"] = id }, ["payment"] = new JsonObject { ["debtor"] = new JsonObject { ["name"] = "TB Synthetic" } } }, new JsonObject(), Now, "test-bank"));
            var hit = Run("p.screen.sanctions.v1", "P-HIT");
            Assert.Equal(("hit", "RR04"), (hit.Outcome, hit.ReasonCode));
            Assert.Equal("hit", (string?)hit.Writes["facts.screening.sanctions"]!["result"]);
            Assert.Equal("clear", Run("p.screen.sanctions.v1", "P-2").Outcome);
            Assert.Equal("TransientError", Assert.Throws<NativeFailure>(() => Run("p.screen.sanctions.v1", "P-DOWN")).Name);
            var fraud = Run("p.screen.fraud.v1", "P-3");
            Assert.Equal(("suspect", "FRAD"), (fraud.Outcome, fraud.ReasonCode));
        }
        finally { Environment.SetEnvironmentVariable("TH_TEST_TOKEN", null); }
    }

    /// <summary>An SMTP relay stand-in: enough of RFC 5321 for one message, with AUTH LOGIN; it keeps what it received.</summary>
    static async Task<(int Port, Task<string> Received)> SmtpStandIn()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        async Task<string> Serve()
        {
            using var c = await l.AcceptTcpClientAsync();
            l.Stop();
            var s = c.GetStream();
            var r = new StreamReader(s, Encoding.ASCII);
            var w = new StreamWriter(s, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            var log = new StringBuilder();
            await w.WriteLineAsync("220 stand-in ESMTP");
            while (await r.ReadLineAsync() is string line)
            {
                log.AppendLine(line);
                var cmd = line.ToUpperInvariant();
                if (cmd.StartsWith("EHLO")) { await w.WriteLineAsync("250-stand-in"); await w.WriteLineAsync("250 AUTH LOGIN"); }
                else if (cmd.StartsWith("AUTH LOGIN"))
                {
                    // The client may give the user with the command (an initial response) or when asked.
                    var inline = line.Length > "AUTH LOGIN".Length ? line["AUTH LOGIN".Length..].Trim() : "";
                    if (inline.Length == 0) { await w.WriteLineAsync("334 VXNlcm5hbWU6"); inline = (await r.ReadLineAsync())!; }
                    log.AppendLine("user=" + Encoding.UTF8.GetString(Convert.FromBase64String(inline)));
                    await w.WriteLineAsync("334 UGFzc3dvcmQ6");
                    log.AppendLine("pass=" + Encoding.UTF8.GetString(Convert.FromBase64String((await r.ReadLineAsync())!)));
                    await w.WriteLineAsync("235 ok");
                }
                else if (cmd.StartsWith("DATA"))
                {
                    await w.WriteLineAsync("354 go on");
                    while (await r.ReadLineAsync() is string d && d != ".") log.AppendLine(d);
                    await w.WriteLineAsync("250 queued");
                }
                else if (cmd.StartsWith("QUIT")) { await w.WriteLineAsync("221 bye"); break; }
                else await w.WriteLineAsync("250 ok");
            }
            return log.ToString();
        }
        return (port, Serve());
    }

    [Fact]
    public async Task Notices_go_by_email_through_the_banks_relay_and_by_sms_through_its_gateway_with_ids_only()
    {
        var (port, received) = await SmtpStandIn();
        var mail = NoticeAdapters.Create(Bind(NoticeAdapters.Port, EmailNotices.Name, new()
        {
            ["host"] = "127.0.0.1", ["port"] = port.ToString(), ["tls"] = "false", ["from"] = "payments@bank.test", ["to"] = "channel-desk@bank.test", ["secretRef"] = "env:TH_TEST_SMTP",
        }), null, Secrets);
        var id = await mail.Send(new Notice("P-1", "ACSC", "Payment P-1: ACSC", "Payment P-1 is now ACSC."));
        var log = await received.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.StartsWith("<", id);
        Assert.Contains("RCPT TO:<channel-desk@bank.test>", log);
        Assert.Contains("user=relay-user", log);
        Assert.Contains("pass=relay-pass", log);
        Assert.Contains("Payment P-1 is now ACSC.", log);

        using var gateway = new StandIn((_, _, _) => (202, """{ "id": "sms-42" }"""));
        var sms = NoticeAdapters.Create(Bind(NoticeAdapters.Port, SmsNotices.Name, new() { ["baseUrl"] = gateway.Url, ["to"] = "+000000000", ["secretRef"] = "env:TH_TEST_TOKEN", ["credentialHeader"] = "X-Api-Key" }), new HttpClient(), Secrets);
        Assert.Equal("sms-42", await sms.Send(new Notice("P-2", "RJCT", "s", "Payment P-2 is now RJCT.")));
        var call = gateway.Calls.Single();
        Assert.Equal("/messages", call.Path);
        Assert.Equal("token-for-test", call.Headers["X-Api-Key"]);
        Assert.Equal("Payment P-2 is now RJCT.", (string?)JsonNode.Parse(call.Body)!["text"]);
    }

    [Fact]
    public async Task The_business_calendar_from_the_reference_table_from_a_service_and_simulated()
    {
        var rows = new JsonArray(new JsonObject { ["day"] = "saturday" }, new JsonObject { ["day"] = "sunday" }, new JsonObject { ["day"] = "2026-12-02" });
        ReferenceReader reader = (tenant, domain, at, ct) => Task.FromResult<(string?, JsonArray?)>(tenant == "test-bank" && domain == "business-calendar" ? ("uae.p.ref.business-calendar.v1", rows) : (null, null));
        var cal = CalendarAdapters.Create(Bind(CalendarAdapters.Port, ReferenceCalendar.Name, new()), reader);
        Assert.Equal(new BusinessDay("2026-11-28", false, "2026-11-30", "uae.p.ref.business-calendar.v1"), await cal.Of(DateTimeOffset.Parse("2026-11-28T10:00:00Z")));
        Assert.Equal("2026-12-03", (await cal.Of(DateTimeOffset.Parse("2026-12-02T10:00:00Z"))).BusinessDate);
        Assert.Equal("2026-11-30", (await CalendarAdapters.Create(Bind(CalendarAdapters.Port, SimulatedCalendar.Name, new())).Of(DateTimeOffset.Parse("2026-11-29T00:00:00Z"))).BusinessDate);
        using var service = new StandIn((_, _, _) => (200, """{ "businessDay": false, "businessDate": "2026-11-30" }"""));
        var rest = CalendarAdapters.Create(Bind(CalendarAdapters.Port, RestCalendar.Name, new() { ["baseUrl"] = service.Url }), null, new HttpClient(), Secrets);
        Assert.Equal("2026-11-30", (await rest.Of(DateTimeOffset.Parse("2026-11-28T10:00:00Z"))).BusinessDate);
        Assert.Equal("?date=2026-11-28", service.Calls.Single().Query);
        // The activity writes the business date of the payment's receipt.
        var bindings = new PortBindings();
        bindings.Add("""
            kind: port-bindings
            tenant: test-bank
            bindings:
              - port: uae.p.port.calendar.v1
                adapter: reference
                validFrom: 2026-11-01T00:00:00Z
            """);
        var act = PlatformPortActivities.All(bindings, reader).Single(a => a.Name == "uae.p.enrich.business-date.v1");
        var r = act.Run(new NativeAttempt(act.Name, "1.0.0", new JsonObject { ["header"] = new JsonObject { ["receivedAt"] = "2026-11-28T09:00:00Z" } }, new JsonObject(), Now, "test-bank"));
        Assert.Equal("2026-11-30", (string?)r.Writes["header.businessDate"]);
    }

    [Fact]
    public async Task The_participant_directory_from_the_reference_table_and_from_a_service()
    {
        var rows = new JsonArray(new JsonObject { ["routingCode"] = "999000001", ["name"] = "TB Synthetic Bank" });
        ReferenceReader reader = (_, domain, _, _) => Task.FromResult<(string?, JsonArray?)>(domain == "participant-directory" ? ("uae.p.ref.participant-directory.v1", rows) : (null, null));
        var dir = ParticipantDirectoryAdapters.Create(Bind(ParticipantDirectoryAdapters.Port, ReferenceDirectory.Name, new()), reader);
        Assert.Equal(new Participant("999000001", true, "TB Synthetic Bank", null, "uae.p.ref.participant-directory.v1"), await dir.Lookup("999000001", Now));
        Assert.False((await dir.Lookup("999000002", Now)).Found);
        using var service = new StandIn((_, path, _) => path.EndsWith("/999000001") ? (200, """{ "name": "TB Synthetic Bank", "bic": "TBSYAEAA" }""") : (404, "{}"));
        var rest = ParticipantDirectoryAdapters.Create(Bind(ParticipantDirectoryAdapters.Port, RestDirectory.Name, new() { ["baseUrl"] = service.Url, ["secretRef"] = "env:TH_TEST_TOKEN" }), null, new HttpClient(), Secrets);
        Assert.Equal("TBSYAEAA", (await rest.Lookup("999000001", Now)).Bic);
        Assert.False((await rest.Lookup("999000002", Now)).Found);
        Assert.Equal("/participants/999000001", service.Calls.First().Path);
        var bindings = new PortBindings();
        bindings.Add("""
            kind: port-bindings
            tenant: test-bank
            bindings:
              - port: uae.p.port.participant-directory.v1
                adapter: reference
                validFrom: 2026-11-01T00:00:00Z
            """);
        var act = PlatformPortActivities.All(bindings, reader).Single(a => a.Name == "uae.p.enrich.participant.v1");
        var r = act.Run(new NativeAttempt(act.Name, "1.0.0", new JsonObject { ["payment"] = new JsonObject { ["debtorAgent"] = new JsonObject { ["clearingMemberId"] = "999000001" }, ["creditorAgent"] = new JsonObject { ["memberId"] = "999000002" } } }, new JsonObject(), Now, "test-bank"));
        Assert.Equal(("unknown", "RC01"), (r.Outcome, r.ReasonCode));
        Assert.True((bool?)r.Writes["facts.participants"]!["debtorAgent"]!["found"]);
    }

    [Fact]
    public async Task Facts_go_out_signed_once_by_their_key()
    {
        var seen = new ConcurrentDictionary<string, bool>();
        using var endpoint = new StandIn((_, _, body) => seen.TryAdd(body, true) ? (201, """{ "id": "evt-1" }""") : (409, "{}"));
        var f = FactsOutAdapters.Create(Bind(FactsOutAdapters.Port, WebhookFactsOut.Name, new() { ["baseUrl"] = endpoint.Url, ["signingKeyRef"] = "env:TH_TEST_SIGN", ["secretRef"] = "env:TH_TEST_TOKEN" }), new HttpClient(), Secrets);
        var fact = new JsonObject { ["paymentId"] = "P-1", ["status"] = new JsonObject { ["code"] = "ACSC" } };
        Assert.Equal("evt-1", await f.Publish("P-1:ACSC", fact));
        Assert.Equal("P-1:ACSC", await f.Publish("P-1:ACSC", fact)); // the endpoint has it: the same fact, not a failure
        var call = endpoint.Calls.First();
        Assert.Equal("P-1:ACSC", call.Headers["Idempotency-Key"]);
        Assert.Equal(FactsOutAdapters.Sign(Encoding.UTF8.GetBytes(call.Body), "signing-key-for-test"), call.Headers["X-TH-Signature"]);
        Assert.Equal("Bearer token-for-test", call.Headers["Authorization"]);
    }

    [Fact]
    public void The_banks_bindings_of_the_production_adapters_load_with_no_host_and_no_key_in_them()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "content-configurations", "banks");
        foreach (var bank in new[] { "dib", "adib", "enbd", "fab" })
        {
            var p = PortBindings.FromDirectory(Path.Combine(dir, bank));
            var mine = p.All.Where(b => b.Port is ScreeningAdapters.Sanctions or ScreeningAdapters.Fraud or NoticeAdapters.Port or FactsOutAdapters.Port or CalendarAdapters.Port or ParticipantDirectoryAdapters.Port or NotificationAdapters.Port or EventStreamAdapters.Port).ToList();
            Assert.Equal(8, mine.Select(b => b.Port).Distinct().Count());
            foreach (var b in mine)
            {
                foreach (var (k, v) in b.Settings)
                {
                    if (k.EndsWith("Ref", StringComparison.Ordinal)) Assert.True(SecretRef.IsRef(v), $"{bank} {b.Port} {k}");
                    if (k is "baseUrl" or "host" or "bootstrapServers") Assert.Contains(".invalid", v);
                    Assert.DoesNotContain(k, new[] { "password", "token", "apiKey", "key" });
                }
            }
        }
    }
}
