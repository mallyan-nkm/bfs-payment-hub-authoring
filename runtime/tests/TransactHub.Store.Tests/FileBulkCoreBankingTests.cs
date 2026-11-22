using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Platform.Posting;
using TransactHub.Ports;
using Xunit;

namespace TransactHub.Store.Tests;

// The file-bulk adapter of the core-banking port (owner's decision of 2026-11-22, Issue I-82, R-12-15): a batch of postings becomes
// one bulk file in the source's DE layout, goes to core banking's landing folder through the bank's file-transfer port, is submitted,
// its status read, and the lines core banking failed are posted again in a file of their own. Accounts and amounts are synthetic.

public sealed class FileBulkCoreBankingTests
{
    static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-11-22T00:00:00Z");
    static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, _) => Task.CompletedTask;

    static PortBinding Binding(Dictionary<string, string>? more = null)
    {
        var s = new Dictionary<string, string>
        {
            ["folder"] = "/cbs/de/in", ["headerBranch"] = "997", ["sourceCode"] = "WPSDE", ["batchDescription"] = "WPS salary credits",
            ["upload"] = "simulator", ["statusPollSeconds"] = "0",
        };
        foreach (var (k, v) in more ?? []) s[k] = v;
        return new PortBinding("test-bank", "p.port.core-banking.v1", FileBulkCoreBanking.Name, From, s);
    }

    static PostingBatch Batch(string reference = "P-1", params (string Kind, string Account, decimal Amount)[] legs) =>
        new(reference, new DateOnly(2026, 11, 24), (legs.Length == 0 ? [("debit", "9990000000002", 1500m), ("credit", "0331234567890", 1000m), ("credit", "0336543210987", 500m)] : legs)
            .Select(l => new PostingLeg(l.Kind, "033", l.Account, l.Amount, "AED", "WPS", $"Salary {reference}")).ToList());

    static (FileBulkCoreBanking Cb, SimulatedFileTransfer Transfer, SimulatedBulkUploadGateway Gateway) Adapter(PortBinding? b = null, SimulatedFileTransfer? t = null, SimulatedBulkUploadGateway? g = null)
    {
        b ??= Binding();
        t ??= new SimulatedFileTransfer();
        g ??= new SimulatedBulkUploadGateway(b, t);
        return (new FileBulkCoreBanking(b, t, g, new SimulatedCoreBanking(b), NoWait), t, g);
    }

    [Fact]
    public async Task A_batch_is_one_file_in_the_source_layout_on_the_landing_folder_and_posts()
    {
        var (cb, t, g) = Adapter();
        var answer = await cb.Post(Batch());
        Assert.True(answer.Posted);
        Assert.Equal("0", answer.Code);
        Assert.Equal("DEP-1.txt", answer.HostReference);
        Assert.Equal(1, g.Submitted);
        var file = Encoding.ASCII.GetString(t.Snapshot()["cbs/de/in/DEP-1.txt"]);
        Assert.Equal(
            "997~WPSDE~WPS salary credits~P-1\r\n" +
            "1~033~9990000000002~AED~20261124~20261124~1500.00~1500.00~D~1.00000~WPS~Salary P-1~~~\r\n" +
            "2~033~0331234567890~AED~20261124~20261124~1000.00~1000.00~C~1.00000~WPS~Salary P-1~~~~\r\n" +
            "3~033~0336543210987~AED~20261124~20261124~500.00~500.00~C~1.00000~WPS~Salary P-1~~~~\r\n", file);
        Assert.All(answer.Lines!, l => Assert.True(l.Posted));
    }

    [Fact]
    public async Task A_line_core_banking_fails_is_posted_again_in_a_file_of_its_own()
    {
        var (cb, t, g) = Adapter(Binding(new() { ["simulator.failAccounts"] = "0336543210987", ["simulator.failTimes"] = "1" }));
        var answer = await cb.Post(Batch());
        Assert.True(answer.Posted);
        Assert.Equal(2, g.Submitted);
        var repost = Encoding.ASCII.GetString(t.Snapshot()["cbs/de/in/DEP-1-R1.txt"]);
        Assert.Equal("997~WPSDE~WPS salary credits~P-1-R1\r\n3~033~0336543210987~AED~20261124~20261124~500.00~500.00~C~1.00000~WPS~Salary P-1~~~~\r\n", repost);
        Assert.Equal(2, answer.Lines!.Single(l => l.Line == 3).Generation);
        Assert.Equal(1, answer.Lines!.Single(l => l.Line == 2).Generation);
    }

    [Fact]
    public async Task A_line_that_fails_after_its_reposts_is_named_and_the_batch_is_not_posted()
    {
        var (cb, _, g) = Adapter(Binding(new() { ["simulator.failAccounts"] = "0336543210987", ["simulator.failTimes"] = "9", ["simulator.failCode"] = "AC04", ["repostAttempts"] = "2" }));
        var answer = await cb.Post(Batch());
        Assert.False(answer.Posted);
        Assert.Equal("AC04", answer.Code);
        Assert.Null(answer.HostReference);
        Assert.Equal(3, g.Submitted);
        var failed = Assert.Single(answer.Lines!, l => !l.Posted);
        Assert.Equal((3, "AC04", 3), (failed.Line, failed.Code, failed.Generation));
    }

    [Fact]
    public async Task Only_the_codes_the_bank_reposts_are_posted_again()
    {
        var (cb, _, g) = Adapter(Binding(new() { ["simulator.failAccounts"] = "0336543210987", ["simulator.failCode"] = "AC06", ["repostCodes"] = "AC04,IF-DE30" }));
        var answer = await cb.Post(Batch());
        Assert.False(answer.Posted);
        Assert.Equal(1, g.Submitted);
    }

    [Fact]
    public async Task The_same_batch_is_the_same_file_and_is_never_uploaded_twice_even_after_a_restart()
    {
        var b = Binding();
        var (cb, t, g) = Adapter(b);
        var first = await cb.Post(Batch());
        Assert.Same(first, await cb.Post(Batch()));
        // A new adapter (the host restarted) on the same landing folder and core banking: "File Already Uploaded" is read as taken.
        var (again, _, _) = Adapter(b, t, g);
        var second = await again.Post(Batch());
        Assert.True(second.Posted);
        Assert.Equal(1, g.Submitted);
        // Another batch is another file.
        await cb.Post(Batch("P-2"));
        Assert.Equal(2, g.Submitted);
    }

    [Fact]
    public async Task A_posting_is_inquired_by_its_batch_from_the_answer_given_or_the_upload_status()
    {
        var b = Binding(new() { ["simulator.failAccounts"] = "0336543210987", ["simulator.failTimes"] = "9", ["repostAttempts"] = "0" });
        var (cb, t, g) = Adapter(b);
        await cb.Post(Batch("P-1"));
        Assert.Equal(new PostingInquiry("P-1", true, false, null), await cb.Inquire("P-1"));
        // After a restart the answer is read from core banking's status of the batch's file.
        var (again, _, _) = Adapter(b, t, g);
        Assert.Equal(new PostingInquiry("P-1", true, false, null), await again.Inquire("P-1"));
        var (ok, _, _) = Adapter();
        await ok.Post(Batch("P-2", ("debit", "9990000000002", 10m), ("credit", "0331234567890", 10m)));
        Assert.Equal(new PostingInquiry("P-2", true, true, "DEP-2.txt"), await ok.Inquire("P-2"));
        Assert.Equal(new PostingInquiry("P-9", false, false, null), await again.Inquire("P-9"));
    }

    [Fact]
    public async Task The_status_is_read_until_core_banking_answers_and_no_answer_is_an_unknown_fate()
    {
        var (cb, _, _) = Adapter(Binding(new() { ["simulator.pendingPolls"] = "2", ["statusPolls"] = "3" }));
        Assert.True((await cb.Post(Batch())).Posted);
        var (slow, _, _) = Adapter(Binding(new() { ["simulator.pendingPolls"] = "5", ["statusPolls"] = "3" }));
        var e = await Assert.ThrowsAsync<PostingFateUnknown>(() => slow.Post(Batch()));
        Assert.IsAssignableFrom<IOException>(e);
    }

    [Fact]
    public async Task A_leg_in_another_currency_is_refused_before_anything_is_sent()
    {
        var (cb, t, g) = Adapter();
        var batch = Batch() with { Legs = [new PostingLeg("credit", "033", "0331234567890", 10m, "USD", "WPS", "x")] };
        var answer = await cb.Post(batch);
        Assert.False(answer.Posted);
        Assert.Equal("CCY", answer.Code);
        Assert.Empty(t.Snapshot());
        Assert.Equal(0, g.Submitted);
    }

    [Fact]
    public void A_field_with_the_separator_or_a_line_break_is_cleaned_and_one_outside_ASCII_is_blank()
    {
        Assert.Equal("a b c", BulkFileLayout.Field("a~b\nc"));
        Assert.Equal("", BulkFileLayout.Field("café"));
    }

    sealed class Recording(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, string Body)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Calls.Add((r, body));
            return answer(r, body);
        }
    }

    static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task On_the_gateway_the_upload_and_its_status_are_the_source_calls_and_a_failed_record_is_its_line()
    {
        var handler = new Recording((r, body) => r.RequestUri!.AbsolutePath.EndsWith("/upload")
            ? Json("""{ "code": "0", "message": "ok" }""")
            : body.Contains("-R1") ? Json("""{ "fault": { "ErrorCode": "12" } }""")
            : Json("""{ "records": [ { "ErrorCode": "0", "EXTERNAL_REF_NO": "2" }, { "ErrorCode": "IF-DE30", "EXTERNAL_REF_NO": "~3~" } ] }"""));
        var b = Binding(new()
        {
            ["upload"] = "gateway-rest", ["baseUrl"] = "https://core-banking.test.invalid/", ["uploadPath"] = "/api/de/upload", ["statusPath"] = "/api/de/status",
            ["uploadFunctionId"] = "0901", ["statusFunctionId"] = "0902", ["uploadUser"] = "WPSUSER", ["uploadBranch"] = "997", ["externalHostId"] = "TEST", ["secretRef"] = "env:TH_TEST_CB_TOKEN",
        });
        var secrets = new TransactHub.Security.SecretResolver([new TransactHub.Security.EnvSecretProvider(k => k == "TH_TEST_CB_TOKEN" ? "token-for-test" : null)]);
        var t = new SimulatedFileTransfer();
        var cb = (FileBulkCoreBanking)CoreBankingAdapters.Create(b, new HttpClient(handler), secrets, () => t, NoWait);
        var answer = await cb.Post(Batch());
        Assert.True(answer.Posted);
        var upload = handler.Calls.First();
        Assert.Equal("""{"functionId":"0901","userId":"WPSUSER","branch":"997","fileName":"DEP-1.txt"}""", upload.Body);
        Assert.Equal("0901", upload.Request.Headers.GetValues("X-Function-Id").Single());
        Assert.Equal("P-1:g1", upload.Request.Headers.GetValues("X-Ref").Single());
        Assert.Equal("Bearer token-for-test", upload.Request.Headers.Authorization!.ToString());
        // Line 3 failed (IF-DE30), was posted again in DEP-1-R1.txt, and that file had no failed record (fault 12).
        Assert.Contains(handler.Calls, c => c.Body.Contains("DEP-1-R1.txt"));
        Assert.Equal(2, answer.Lines!.Single(l => l.Line == 3).Generation);
        Assert.Equal(UploadStatusKind.FileNotFound, RestBulkUploadGateway.Read(JsonNode.Parse("""{ "fault": [ { "ErrorCode": 901 } ] }""")!.AsObject()).Kind);
        Assert.Equal(UploadStatusKind.Empty, RestBulkUploadGateway.Read(JsonNode.Parse("""{ "records": [] }""")!.AsObject()).Kind);
    }

    [Fact]
    public async Task An_already_uploaded_file_on_the_gateway_is_read_for_its_status_and_a_refused_one_posts_nothing()
    {
        var already = new Recording((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/upload") ? Json("""{ "code": "1", "message": "File Already Uploaded" }""") : Json("""{ "fault": { "ErrorCode": "12" } }"""));
        var refused = new Recording((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/upload") ? Json("""{ "code": "1", "message": "invalid file operation" }""") : Json("{}"));
        var b = Binding(new()
        {
            ["upload"] = "gateway-rest", ["baseUrl"] = "https://core-banking.test.invalid/", ["uploadPath"] = "/api/de/upload", ["statusPath"] = "/api/de/status",
            ["uploadFunctionId"] = "0901", ["statusFunctionId"] = "0902", ["uploadUser"] = "WPSUSER", ["uploadBranch"] = "997", ["externalHostId"] = "TEST",
        });
        Assert.True((await CoreBankingAdapters.Create(b, new HttpClient(already), null, () => new SimulatedFileTransfer(), NoWait).Post(Batch())).Posted);
        var no = await CoreBankingAdapters.Create(b, new HttpClient(refused), null, () => new SimulatedFileTransfer(), NoWait).Post(Batch());
        Assert.False(no.Posted);
        Assert.All(no.Lines!, l => Assert.False(l.Posted));
        Assert.Single(refused.Calls);
    }

    const string Bindings = """
        kind: port-bindings
        tenant: bulk-bank
        bindings:
          - port: p.port.core-banking.v1
            adapter: file-bulk
            validFrom: 2026-11-22T00:00:00Z
            settings:
              upload: simulator
              folder: /cbs/de/in
              headerBranch: "997"
              sourceCode: WPSDE
              batchDescription: WPS salary credits
              statusPollSeconds: "0"
              simulator.failAccounts: "0336543210987"
              simulator.failTimes: "5"
              repostAttempts: "1"
          - port: p.port.file-transfer.v1
            adapter: simulator
            validFrom: 2026-11-22T00:00:00Z
        """;

    [Fact]
    public void A_bank_chooses_it_in_its_binding_and_the_posting_activity_is_unchanged()
    {
        var all = new PortBindings();
        all.Add(Bindings);
        all.Add(Bindings.Replace("bulk-bank", "no-transfer-bank").Replace("p.port.file-transfer.v1", "p.port.channel.v1"));
        var post = PlatformActivities.CoreBanking(new CoreBankingResolver(all, NoWait)).Single(a => a.Name == "p.post.accounting.v1");
        JsonObject View(string account) => (JsonObject)JsonNode.Parse($$"""
            { "header": { "paymentId": "P-9", "direction": "inward", "businessDate": "2026-11-24" },
              "payment": { "creditorAccount": { "id": "{{account}}" }, "instructedAmount": { "value": 1000, "currency": "AED" } } }
            """)!;
        var p = (JsonObject)JsonNode.Parse("""
            { "template": "default", "transactionCode": "WPS", "narration": "Salary", "settlementAccount": "9990000000001", "chargeSlabs": [], "vatPercent": 0 }
            """)!;
        var now = DateTimeOffset.Parse("2026-11-24T06:00:00Z");
        var ok = post.Run(new NativeAttempt(post.Name, "1.1.0", View("0331234567890"), p, now, "bulk-bank", "item"));
        Assert.Equal("posted", ok.Outcome);
        Assert.Equal("file-bulk", (string?)ok.Writes["facts.posting"]!["adapter"]);
        Assert.Equal(2, ok.Writes["facts.posting"]!["lines"]!.AsArray().Count);
        var failed = post.Run(new NativeAttempt(post.Name, "1.1.0", View("0336543210987"), p, now, "bulk-bank", "item"));
        Assert.Equal("rejected", failed.Outcome);
        Assert.Contains(failed.Writes["facts.posting"]!["lines"]!.AsArray(), l => (bool)l!["posted"]! == false && (string?)l["code"] == "AC04");
        var e = Assert.Throws<NativeFailure>(() => post.Run(new NativeAttempt(post.Name, "1.1.0", View("0331234567890"), p, now, "no-transfer-bank", "item")));
        Assert.Equal("PortNotBound", e.Name);
    }

    [Fact]
    public async Task A_directory_transfer_writes_under_its_root_by_reference_and_refuses_a_name_that_climbs_out()
    {
        var root = Directory.CreateTempSubdirectory("th-ft-");
        try
        {
            var secrets = new TransactHub.Security.SecretResolver([new TransactHub.Security.EnvSecretProvider(k => k == "TH_TEST_FT_ROOT" ? root.FullName : null)]);
            var t = FileTransferAdapters.Create(new PortBinding("test-bank", FileTransferAdapters.Port, DirectoryFileTransfer.Name, From, new Dictionary<string, string> { ["rootRef"] = "env:TH_TEST_FT_ROOT" }), secrets);
            await t.Put("/cbs/de/in", "a.txt", [1, 2, 3]);
            Assert.Equal([1, 2, 3], await t.Get("cbs/de/in", "a.txt"));
            Assert.Equal(["a.txt"], await t.List("/cbs/de/in"));
            Assert.True(await t.Delete("/cbs/de/in", "a.txt"));
            await Assert.ThrowsAsync<PortBindingException>(() => t.Put("/cbs/../etc", "x", [0]));
            await Assert.ThrowsAsync<PortBindingException>(() => t.Put("/cbs", "../x", [0]));
            Assert.Throws<PortBindingException>(() => FileTransferAdapters.Create(new PortBinding("test-bank", FileTransferAdapters.Port, "sftp", From, new Dictionary<string, string>())));
        }
        finally { root.Delete(true); }
    }
}
