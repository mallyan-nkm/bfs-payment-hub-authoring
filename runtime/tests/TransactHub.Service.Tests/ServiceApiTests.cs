using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace TransactHub.Service.Tests;

/// <summary>
/// Who may call the service console (R-06-25) and what each read endpoint answers (R-09-39, R-09-42, R-05-52): every route
/// refuses a caller who is not signed in (401) and one whose role map gives no service role (403), with the error's code
/// and its runbook; the dev sign-in is refused in Production.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ServiceApiTests(Databases dbs)
{
    static readonly (string method, string path, object? body)[] Routes =
    [
        ("GET", "/service/api/me", null), ("GET", "/service/api/health", null),
        ("GET", "/service/api/content/versions", null), ("GET", "/service/api/content/releases", null), ("GET", "/service/api/content/open-payments", null),
        ("GET", "/service/api/content/diff?tag=release/p.wf.demo.v1/1.0.0/temporal", null),
        ("POST", "/service/api/content/verify", new { tag = "release/p.wf.demo.v1/1.0.0/temporal", reason = "check" }),
        ("POST", "/service/api/content/install", new { tag = "release/p.wf.demo.v1/1.0.0/temporal", reason = "install" }),
        ("POST", "/service/api/backup", new { reason = "nightly" }), ("POST", "/service/api/backup/verify", null), ("POST", "/service/api/restore?dryRun=true&reason=x", null),
        ("GET", "/service/api/logs", null), ("GET", "/service/api/payments/none/trace", null), ("GET", "/service/api/audit", null),
        ("GET", "/service/api/errors", null), ("GET", "/service/api/errors/TransientError", null),
        ("GET", "/service/api/actions", null), ("GET", "/service/api/actions/pending", null),
        ("POST", "/service/api/actions", new { action = "replay-read-model", reason = "check" }),
        ("POST", "/service/api/actions/none/decision", new { approve = true, reason = "ok" }),
        ("GET", "/service/api/assistant", null), ("POST", "/service/api/assistant", new { question = "why" }),
    ];

    static HttpRequestMessage Req(string method, string path, object? body) =>
        new(new HttpMethod(method), path) { Content = body is null ? new ByteArrayContent([]) : JsonContent.Create(body) };

    [Fact]
    public async Task Every_route_needs_a_signed_in_caller_with_a_service_role()
    {
        using var s = new Signers();
        var gh = new FakeGitHub();
        Packages.Publish(gh, s.Key, "p.wf.demo.v1", "1.0.0");
        await using var api = new ServiceApi(dbs.Fresh(), s, gh);
        var anonymous = api.CreateClient();
        var operatorOnly = api.As("op-1", "example-bank:Operator");
        foreach (var (method, path, body) in Routes)
        {
            var r = await anonymous.SendAsync(Req(method, path, body));
            Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path}: {r.StatusCode}");
            if (path.EndsWith("/me")) continue;
            var f = await operatorOnly.SendAsync(Req(method, path, body));
            Assert.True(f.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} as an operator: {f.StatusCode}");
            var j = await J.Of(f);
            Assert.Equal("auth.Forbidden", (string?)j["code"]);
            Assert.Equal("#/service/errors/auth.Forbidden", (string?)j["runbook"]);
        }
        // A decision needs a bank approver, not an engineer.
        var d = await api.Engineer.SendAsync(Req("POST", "/service/api/actions/none/decision", new { approve = true, reason = "ok" }));
        Assert.Equal(HttpStatusCode.Forbidden, d.StatusCode);
        var me = await J.Of(await api.Engineer.GetAsync("/service/api/me"));
        Assert.True((bool)me["service"]!);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await api.Engineer.DeleteAsync("/service/api/content/releases")).StatusCode);
        Assert.Equal("dev", (string?)(await J.Of(await anonymous.GetAsync("/service/api/auth/config")))["mode"]);
    }

    [Fact]
    public async Task The_dev_sign_in_is_refused_in_Production_and_the_studios_issuer_is_never_trusted()
    {
        using var s = new Signers();
        await using (var prod = new ServiceApi(dbs.Fresh(), s, new FakeGitHub(), environment: "Production"))
            Assert.Contains("TH_AUTH_DEV=1 is refused in Production", Assert.ThrowsAny<Exception>(() => prod.CreateClient()).ToString());
        await using var studio = new ServiceApi(dbs.Fresh(), s, new FakeGitHub(), new() { ["TH_AUTH_DEV"] = "0", ["Auth:Oidc:Issuer"] = "https://studio.example.test/", ["TH_STUDIO_ISSUER"] = "https://studio.example.test", ["Auth:Oidc:Audience"] = "x", ["Auth:RoleMap"] = "{}" });
        Assert.Contains("the studio's issuer", Assert.ThrowsAny<Exception>(() => studio.CreateClient()).ToString());
    }

    [Fact]
    public async Task Health_shows_the_store_the_read_model_the_source_the_keys_and_disk_with_a_history()
    {
        using var s = new Signers();
        var gh = new FakeGitHub();
        Packages.Publish(gh, s.Key, "p.wf.demo.v1", "1.0.0");
        await using var api = new ServiceApi(dbs.Fresh(), s, gh, new()
        {
            ["Service:Probes:0:Name"] = "core-banking", ["Service:Probes:0:Group"] = "ports", ["Service:Probes:0:Kind"] = "tcp", ["Service:Probes:0:Target"] = "127.0.0.1:1",
            ["Service:Dashboards:Payments"] = "http://grafana.example.test/d/payments",
        });
        var c = api.Engineer;
        await c.GetAsync("/service/api/health?refresh=1");
        var h = await J.Of(await c.GetAsync("/service/api/health?refresh=1"));
        var comps = h["components"]!.AsArray().ToDictionary(x => (string)x!["name"]!);
        Assert.Equal("ok", (string?)comps["store"]!["status"]);
        Assert.Equal("ok", (string?)comps["read-model"]!["status"]);
        Assert.Equal("ok", (string?)comps["package-source"]!["status"]);
        Assert.Contains("2 signing key(s) in use", (string?)comps["trusted-signers"]!["detail"]);
        Assert.Contains(comps.Keys, k => k.StartsWith("disk:"));
        // A port that does not answer fails with its code and its runbook.
        Assert.Equal("fail", (string?)comps["core-banking"]!["status"]);
        Assert.Equal("service.ProbeFailed", (string?)comps["core-banking"]!["code"]);
        Assert.Equal("#/service/errors/service.ProbeFailed", (string?)comps["core-banking"]!["runbook"]);
        Assert.True(comps["store"]!["history"]!.AsArray().Count >= 2);
        Assert.Equal("fail", (string?)h["status"]);
        Assert.Equal("http://grafana.example.test/d/payments", (string?)h["dashboards"]!["Payments"]);
    }

    [Fact]
    public async Task Logs_show_the_facts_masked_filtered_and_linked_to_the_payment_and_each_error_to_its_runbook()
    {
        using var s = new Signers();
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub());
        var c = api.Engineer;
        await c.GetAsync("/service/api/me");
        var bulk = await Seed.Bulk(api.Store, "p.wf.corporate.v1");
        var other = await Seed.Bulk(api.Store, "p.wf.corporate.v1", tenant: "other-bank");
        await api.CatchUp();

        var am02 = await J.Of(await c.GetAsync("/service/api/logs?code=AM02&scheme=uae.wps"));
        var item = Assert.Single(am02["items"]!.AsArray(), i => (string?)i!["subject"] == bulk)!;
        Assert.Equal("Error", (string?)item["level"]);
        Assert.Equal($"#/payments/{bulk}", (string?)item["payment"]);
        var err = item["errors"]!.AsArray().Single()!;
        Assert.Equal("AM02", (string?)err["code"]);
        Assert.Equal("#/service/errors/AM02", (string?)err["runbook"]);
        var text = am02.ToJsonString();
        Assert.DoesNotContain(Seed.Iban, text);
        Assert.Contains("****3456", text);

        var leg = await J.Of(await c.GetAsync($"/service/api/logs?payment={bulk}&code=TransientError"));
        Assert.Equal("TransientError", (string?)leg["items"]![0]!["errors"]![0]!["code"]);
        // Time filters, and the tenant bound: an engineer of one tenant sees only its payments.
        Assert.Equal(0, (int)(await J.Of(await c.GetAsync($"/service/api/logs?payment={bulk}&to=2000-01-01T00:00:00Z")))["count"]!);
        var mine = api.As("se-2", "example-bank:ServiceEngineer");
        Assert.DoesNotContain(other, (await J.Of(await mine.GetAsync("/service/api/logs?source=facts"))).ToJsonString());
        Assert.Equal(HttpStatusCode.Forbidden, (await mine.GetAsync("/service/api/logs?tenant=other-bank")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mine.GetAsync($"/service/api/payments/{other}/trace")).StatusCode);
        var trace = await J.Of(await mine.GetAsync($"/service/api/payments/{bulk}/trace"));
        Assert.Equal($"#/payments/{bulk}", (string?)trace["operationsConsole"]);
    }

    [Fact]
    public async Task The_dictionary_shows_each_entry_with_its_runbook_and_the_actions_it_relies_on()
    {
        using var s = new Signers();
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub());
        var c = api.Engineer;
        var all = (await J.Of(await c.GetAsync("/service/api/errors"))).AsArray();
        Assert.Contains(all, e => (string?)e!["code"] == "FF01" && (string?)e["scope"] == "uae.wps");
        var e = await J.Of(await c.GetAsync("/service/api/errors/RetriesExhausted"));
        Assert.False((bool)e["retryable"]!);
        Assert.NotEmpty(e["runbook"]!["checks"]!.AsArray());
        var offered = e["actionsOffered"]!.AsArray().ToDictionary(a => (string)a!["id"]!);
        Assert.Equal("built", (string?)offered["redrive-workflow"]!["path"]);
        Assert.Equal("built", (string?)offered["requeue-dead-letter"]!["path"]);
        // A scheme's own entry first.
        Assert.Equal("uae.wps", (string?)(await J.Of(await c.GetAsync("/service/api/errors/AM10?scheme=uae.wps")))["scope"]);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/service/api/errors/NOPE")).StatusCode);
    }
}
