using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TransactHub.Operations.Api;
using TransactHub.Operations;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Operations.Tests;

/// <summary>
/// Reports, the dashboard and the CSV export of the operations console (R-05-59, Issue I-68): built from the read model and
/// the operations views (the WPS view's declared columns and filters, not a WPS screen), for the tenants the caller holds the
/// operator's role for, and with personal fields masked as the views declare.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ReportTests(Databases dbs)
{
    sealed class Host(StoreOptions store) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseEnvironment("Development");
            b.UseSetting("Store:Provider", store.Provider.ToString());
            b.UseSetting("Store:Connection", store.ConnectionString);
            b.UseSetting("Operations:Views", Path.Combine(Repo.Root(), "content-configurations", "artefacts"));
            b.UseSetting("Operations:PollMilliseconds", "100");
            b.UseSetting("TH_AUTH_DEV", "1");
        }
    }

    static HttpClient As(HttpClient c, string sub, params string[] roles)
    {
        c.DefaultRequestHeaders.Add(OperatorAuth.DevHeader, new JsonObject { ["sub"] = sub, ["name"] = sub, ["roles"] = new JsonArray(roles.Select(r => (JsonNode?)r).ToArray()) }.ToJsonString());
        return c;
    }

    static async Task<JsonNode> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task Reports_and_the_export_come_from_the_views_and_the_read_model_for_the_callers_tenants_masked(StoreProvider p)
    {
        var (store, ops) = await dbs.Pair(p);
        await WpsRun.Seed(store);
        await WpsRun.Seed(store, fileId: "0000001234567261117083222", hold: false, ack: "nak");
        var other = await WpsRun.Seed(store, tenant: "other-bank", fileId: "0000001234567261117083333");
        await new Projector(store, ops, Repo.Views()) { GapTimeout = TimeSpan.Zero }.CatchUp();
        await using var host = new Host(store);
        var c = As(host.CreateClient(), "op-1", "example-bank:Operator");

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/ops/api/reports/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/ops/api/reports/export.csv?tenant=other-bank")).StatusCode);

        // What a report may group by: the read model's dimensions and each view's exact filters.
        var meta = await Json(await c.GetAsync("/ops/api/reports"));
        Assert.Contains("status", meta["dimensions"]!.AsArray().Select(x => (string?)x));
        Assert.Contains("field:uae.wps.view.operations.v1:subject:salaryMonth", meta["fields"]!.AsArray().Select(x => (string?)x!["key"]));

        // A summary by scheme, and by a view's field: the caller's tenant only.
        var byScheme = await Json(await c.GetAsync("/ops/api/reports/summary?by=scheme"));
        Assert.Equal(2, (int)byScheme["payments"]!);
        var wps = Assert.Single(byScheme["groups"]!.AsArray())!;
        Assert.Equal(("uae.wps", 2), ((string?)wps["key"], (int)wps["payments"]!));
        Assert.Equal(3000m, (decimal)wps["amounts"]![0]!["amount"]!);
        var byMonth = await Json(await c.GetAsync("/ops/api/reports/summary?by=field:uae.wps.view.operations.v1:subject:salaryMonth"));
        Assert.Equal(2, byMonth["groups"]!.AsArray().Sum(g => (int)g!["payments"]!));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/ops/api/reports/summary?by=field:uae.wps.view.operations.v1:subject:debitAccount")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/ops/api/reports/summary?by=password")).StatusCode);

        // The dashboard: totals, and for the WPS view the measures and filters it declares.
        var dash = await Json(await c.GetAsync("/ops/api/reports/dashboard"));
        Assert.Equal(2, (int)dash["payments"]!);
        var view = Assert.Single(dash["schemes"]!.AsArray())!;
        Assert.Equal("uae.wps", (string?)view["scheme"]);
        Assert.Contains("totalAmount", view["measures"]!.AsArray().Select(m => (string?)m!["id"]));
        Assert.Contains("employerId", view["dimensions"]!.AsArray().Select(d => (string?)d!["id"]));
        Assert.DoesNotContain(other, dash.ToJsonString());

        // The export: the read model's columns and the views' list columns, masked, as CSV; another tenant's payment is not in it.
        var r = await c.GetAsync("/ops/api/reports/export.csv?scheme=uae.wps");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("text/csv", r.Content.Headers.ContentType!.ToString());
        Assert.Equal("2", r.Headers.GetValues("x-th-rows").Single());
        var csv = await r.Content.ReadAsStringAsync();
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Id,Kind,Tenant,Scheme,Direction,Status,Last outcome,Hold,Accepted at,Distribution,Amount,Currency,Lines,", lines[0]);
        Assert.Contains("MOL establishment id", lines[0]);
        Assert.DoesNotContain(other, csv);
        Assert.DoesNotContain("0000001234567,", csv);
        Assert.Contains(Mask.Apply("id", "0000001234567")!, csv);
        // Someone with no operator role exports nothing.
        var none = As(host.CreateClient(), "op-2", "example-bank:Auditor");
        var empty = await (await none.GetAsync("/ops/api/reports/export.csv")).Content.ReadAsStringAsync();
        Assert.Single(empty.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }
}
