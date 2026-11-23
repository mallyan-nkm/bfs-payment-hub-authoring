using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TransactHub.Operations;
using TransactHub.Operations.Api;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Operations.Tests;

/// <summary>
/// The operations console's upload page (R-05-73, R-05-74, Issue I-201) and its audit page (R-05-75, Issue I-202), through the
/// console's API. The upload offers the messages the distribution active in the console's environment receives, posts the
/// person's file to the File API's <c>POST /files/ui</c> with the person's own token, and answers kept, refused or started, with
/// the runbook of a refusal's code and the payment a start began. Here the File API is a stand-in that records what it was sent.
/// The audit is the console's own chain, newest first, filtered by person, action and date, each row with its hash, and says
/// whether the chain verifies.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class UploadAndAuditTests(Databases dbs)
{
    const string Bank = "example-bank";
    const string Dist = "2026.11.23-1";

    /// <summary>The File API, as far as the console sees it: it answers what the test says and keeps what it was sent.</summary>
    sealed class FakeFileApi(Func<string, (HttpStatusCode, JsonObject)> answer) : HttpMessageHandler
    {
        public readonly List<(string Path, string? Authorization, string Form)> Sent = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var form = await r.Content!.ReadAsStringAsync(ct);
            Sent.Add((r.RequestUri!.AbsolutePath, r.Headers.Authorization?.ToString(), form));
            var (status, body) = answer(form);
            return new HttpResponseMessage(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    sealed class Host(StoreOptions store, FakeFileApi? fileApi = null, string? bank = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Development");
            b.UseSetting("Store:Provider", store.Provider.ToString());
            b.UseSetting("Store:Connection", store.ConnectionString);
            b.UseSetting("Operations:Views", Path.Combine(Repo.Root(), "content-configurations", "artefacts"));
            b.UseSetting("Operations:PollMilliseconds", "100");
            b.UseSetting("Operations:Environment", "test");
            b.UseSetting("TH_AUTH_DEV", "1");
            if (bank is not null) b.UseSetting("TH_BANK", bank);
            if (fileApi is not null)
                b.ConfigureTestServices(s => s.AddSingleton(new FileApiUploads(new Uri("http://file-api.test/"), "http://service.test", fileApi)));
        }
    }

    static HttpClient As(HttpClient c, string sub, params string[] roles)
    {
        c.DefaultRequestHeaders.Add(OperatorAuth.DevHeader, new JsonObject { ["sub"] = sub, ["name"] = sub, ["roles"] = new JsonArray(roles.Select(r => (JsonNode?)r).ToArray()) }.ToJsonString());
        return c;
    }

    static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    /// <summary>A synthetic distribution's package: two releases of one scheme manifest (the later counts) and a workflow.</summary>
    static byte[] Package()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
            Add("schemes/uae.demo@1.0.0.yaml", "kind: scheme\nscope: uae.demo\nrelease: 1.0.0\ntitle: Demo\nmessages:\n  - id: uae.demo.old-file\n    title: Gone in 1.1.0\n    direction: in\n    disposition:\n      start: { kind: payment }\n");
            Add("schemes/uae.demo@1.1.0.yaml", string.Join('\n',
                "kind: scheme", "scope: uae.demo", "release: 1.1.0", "title: Demo", "messages:",
                "  - id: uae.demo.credit-file", "    title: Credit file from the corporate customer", "    direction: in", "    disposition:", "      start: { kind: payment, leg: uae.demo.wf.credit-file-out.v1 }",
                "  - id: uae.demo.submission", "    title: Sent to the clearing house", "    direction: out", "    disposition:", "      sentBy: uae.demo.exchange.send-file.v1",
                "  - id: uae.demo.ack", "    title: Acknowledgement", "    direction: in", "    disposition:", "      deliver: { correlateBy: file id }", ""));
            Add("workflows/p.wf.corporate.v1.yaml", "kind: workflow\nname: p.wf.corporate.v1\nrelease: 1.0.0\n");
        }
        return ms.ToArray();
    }

    static async Task Activate(StoreOptions store)
    {
        await using var db = StoreDbContext.Open(store);
        var reg = new DistributionRegistry(db);
        await reg.Store(Bank, Dist, Package(), new JsonObject { ["id"] = Dist, ["bank"] = Bank }, "00", "pipeline", "synthetic distribution");
        await reg.Activate(DistributionRegistry.IdOf(Bank, Dist), "test", "pipeline", "synthetic activation");
    }

    static MultipartFormDataContent Form(string tenant, string message, string fileName, string content)
    {
        var f = new MultipartFormDataContent { { new StringContent(tenant), "tenant" }, { new StringContent(message), "message" } };
        f.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", fileName);
        return f;
    }

    [Fact]
    public void The_messages_offered_are_those_the_latest_scheme_manifests_receive()
    {
        var m = UploadCatalogue.Messages(Package());
        Assert.Equal([("uae.demo.ack", "deliver"), ("uae.demo.credit-file", "start")], m.Select(x => (x.Id, x.Kind)));
        Assert.Equal("Credit file from the corporate customer", m.Single(x => x.Id == "uae.demo.credit-file").Title);
    }

    [Fact]
    public async Task An_upload_goes_to_the_file_apis_ui_route_with_the_persons_token_and_answers_kept_refused_or_started()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        var fileApi = new FakeFileApi(form => form.Contains("DEMO-DUP")
            ? (HttpStatusCode.Conflict, new JsonObject { ["status"] = "refused", ["file"] = "f-2", ["code"] = "file.Duplicate", ["error"] = "The same file was received before" })
            : form.Contains("uae.demo.ack")
                ? (HttpStatusCode.Accepted, new JsonObject { ["status"] = "accepted", ["file"] = "f-3", ["detail"] = "kept; no front door is configured" })
                : (HttpStatusCode.Accepted, new JsonObject { ["status"] = "accepted", ["file"] = "f-1", ["payment"] = "pay-demo-1", ["distribution"] = $"dist/{Bank}@{Dist}" }));
        await using var host = new Host(store, fileApi, bank: Bank);
        var op = As(host.CreateClient(), "op-1", $"{Bank}:Operator");
        op.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "person-token");

        // No distribution is active yet: nothing is offered, and an upload is refused before the File API is asked.
        var none = await Json(await op.GetAsync("/ops/api/upload"));
        Assert.Null(none["targets"]![0]!["distribution"]);
        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsync("/ops/api/upload", Form(Bank, "uae.demo.credit-file", "DEMO-0001.csv", "H,DEMO-0001"))).StatusCode);
        await Activate(store);

        var offer = await Json(await op.GetAsync("/ops/api/upload"));
        Assert.True((bool)offer["fileApi"]!);
        var target = offer["targets"]!.AsArray().Single()!;
        Assert.Equal((Bank, $"dist/{Bank}@{Dist}"), ((string?)target["tenant"], (string?)target["distribution"]));
        Assert.Equal(["uae.demo.ack", "uae.demo.credit-file"], target["messages"]!.AsArray().Select(x => (string?)x!["id"]));

        var started = await op.PostAsync("/ops/api/upload", Form(Bank, "uae.demo.credit-file", "DEMO-0001.csv", "H,DEMO-0001"));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var s = await Json(started);
        Assert.Equal(("started", "pay-demo-1", 202), ((string?)s["outcome"], (string?)s["payment"], (int)s["httpStatus"]!));
        var sent = Assert.Single(fileApi.Sent);
        Assert.Equal("/files/ui", sent.Path);
        Assert.Equal("Bearer person-token", sent.Authorization);
        Assert.Contains("DEMO-0001.csv", sent.Form);
        Assert.Contains(Bank, sent.Form);

        var kept = await Json(await op.PostAsync("/ops/api/upload", Form(Bank, "uae.demo.ack", "DEMO-0001.ACK", "ACK")));
        Assert.Equal("kept", (string?)kept["outcome"]);

        var refused = await op.PostAsync("/ops/api/upload", Form(Bank, "uae.demo.credit-file", "DEMO-DUP.csv", "H,DEMO-DUP"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var r = await Json(refused);
        Assert.Equal(("refused", "file.Duplicate", "http://service.test/#/service/errors/file.Duplicate"), ((string?)r["outcome"], (string?)r["code"], (string?)r["runbook"]));

        // A message the distribution does not receive, a message it sends, and another bank's file are refused by the console.
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsync("/ops/api/upload", Form(Bank, "uae.demo.submission", "x.csv", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsync("/ops/api/upload", Form(Bank, "uae.demo.old-file", "x.csv", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsync("/ops/api/upload", Form("other-bank", "uae.demo.credit-file", "x.csv", "x"))).StatusCode);
        Assert.Equal(3, fileApi.Sent.Count);
        // A person with no operator's role in the runtime's bank is offered nothing.
        Assert.Equal(HttpStatusCode.Forbidden, (await As(host.CreateClient(), "ba-1", $"{Bank}:BankApprover").GetAsync("/ops/api/upload")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(host.CreateClient(), "op-9", "other-bank:Operator").GetAsync("/ops/api/upload")).StatusCode);
    }

    [Fact]
    public async Task Without_the_file_api_the_upload_is_refused_and_says_what_to_set()
    {
        var (store, _) = await dbs.Pair(StoreProvider.PostgreSql);
        await Activate(store);
        await using var host = new Host(store);
        var op = As(host.CreateClient(), "op-1", $"{Bank}:Operator");
        Assert.False((bool)(await Json(await op.GetAsync("/ops/api/upload")))["fileApi"]!);
        var r = await op.PostAsync("/ops/api/upload", Form(Bank, "uae.demo.credit-file", "DEMO-0001.csv", "H"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Contains("Operations:FileApi:Url", (string?)(await Json(r))["error"]);
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task The_audit_page_reads_the_consoles_chain_newest_first_filtered_each_row_with_its_hash_and_says_whether_it_verifies(StoreProvider p)
    {
        var (store, _) = await dbs.Pair(p);
        await using var host = new Host(store);
        var sup = As(host.CreateClient(), "sup-1", $"{Bank}:Supervisor");
        // Three facts of the console's own: two exports by an operator, a rebuild a supervisor asks for.
        var op = As(host.CreateClient(), "op-1", $"{Bank}:Operator");
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/ops/api/reports/export.csv?scheme=uae.wps")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/ops/api/reports/export.csv?scheme=uae.wps&status=ACSC")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await sup.PostAsJsonAsync("/ops/api/rebuild", new { remarks = "after a restore (synthetic)" })).StatusCode);

        var all = await Json(await sup.GetAsync("/ops/api/audit"));
        var rows = all["rows"]!.AsArray();
        Assert.Equal([3, 2, 1], rows.Select(r => (int)r!["seq"]!));
        Assert.Equal(["consent", "export", "export"], rows.Select(r => (string?)r!["kind"]));
        Assert.All(rows, r => Assert.Matches("^[0-9a-f]{64}$", (string)r!["hash"]!));
        Assert.Equal((string?)rows[1]!["hash"], (string?)rows[0]!["prevHash"]);
        Assert.True((bool)all["chain"]!["ok"]!);
        Assert.Equal(3, (int)all["chain"]!["facts"]!);

        Assert.Equal([2, 1], (await Json(await sup.GetAsync("/ops/api/audit?person=OP-1")))["rows"]!.AsArray().Select(r => (int)r!["seq"]!));
        Assert.Equal([3], (await Json(await sup.GetAsync("/ops/api/audit?action=rebuild-read-model")))["rows"]!.AsArray().Select(r => (int)r!["seq"]!));
        Assert.Equal([2, 1], (await Json(await sup.GetAsync("/ops/api/audit?action=export")))["rows"]!.AsArray().Select(r => (int)r!["seq"]!));
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        Assert.Equal(3, (await Json(await sup.GetAsync($"/ops/api/audit?from={today}&to={today}")))["rows"]!.AsArray().Count);
        Assert.Empty((await Json(await sup.GetAsync("/ops/api/audit?to=2020-01-01")))["rows"]!.AsArray());

        // A bank approver reads it too; an operator does not.
        Assert.Equal(HttpStatusCode.OK, (await As(host.CreateClient(), "ba-1", $"{Bank}:BankApprover").GetAsync("/ops/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/ops/api/audit")).StatusCode);

        // A fact changed behind the store's back (raw SQL, as an intruder would): the page says the chain does not verify.
        await using (var db = StoreDbContext.Open(store))
        {
            var table = db.Model.FindEntityType(typeof(FactRow))!.GetTableName();
            await db.Database.ExecuteSqlRawAsync(p == StoreProvider.SqlServer
                ? $"UPDATE [{table}] SET [Code] = 'xlsx' WHERE [SubjectId] = '{ConsoleRecord.Subject}' AND [Seq] = 2"
                : $"UPDATE \"{table}\" SET \"Code\" = 'xlsx' WHERE \"SubjectId\" = '{ConsoleRecord.Subject}' AND \"Seq\" = 2");
        }
        var broken = await Json(await sup.GetAsync("/ops/api/audit"));
        Assert.False((bool)broken["chain"]!["ok"]!);
        Assert.Contains("fact 2 was changed", (string?)broken["chain"]!["problem"]);
    }
}
