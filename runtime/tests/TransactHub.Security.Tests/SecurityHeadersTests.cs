using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using TransactHub.Auth;

namespace TransactHub.Security.Tests;

// R-09-60: every runtime host answers with security headers, the console's page with a content security policy that lets it
// talk only to its own APIs and the bank's identity provider; a console host limits changes per client address.
public sealed class SecurityHeadersTests
{
    static async Task<(WebApplication app, HttpClient client)> Host(Dictionary<string, string?> settings, bool limitWrites = true)
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        b.Configuration.AddInMemoryCollection(settings);
        var app = b.Build();
        app.UseRuntimeSecurityHeaders(app.Configuration, limitWrites);
        app.MapGet("/", () => Results.Content("<!doctype html><title>console</title>", "text/html"));
        app.MapGet("/ops/api/x", () => Results.Ok(new { ok = true }));
        app.MapPost("/ops/api/act", () => Results.Ok());
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task The_page_may_reach_only_itself_and_the_bank_s_identity_provider_and_data_allows_nothing()
    {
        var (app, c) = await Host(new() { ["Auth:Oidc:Issuer"] = "https://idp.bank.example.test/realms/ops" });
        await using var _ = app;
        var page = await c.GetAsync("/");
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("connect-src 'self' https://idp.bank.example.test;", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        var data = await c.GetAsync("/ops/api/x");
        Assert.Equal(SecurityHeaders.DataCsp, data.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-store", data.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task A_console_host_limits_changes_per_client_address_and_a_machine_host_does_not()
    {
        var (app, c) = await Host(new() { ["RateLimits:WritesPerMinute"] = "3" });
        await using (app)
        {
            var codes = new List<HttpStatusCode>();
            for (var i = 0; i < 5; i++) codes.Add((await c.PostAsync("/ops/api/act", null)).StatusCode);
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/ops/api/x")).StatusCode);
        }
        var (machine, m) = await Host(new() { ["RateLimits:WritesPerMinute"] = "3" }, limitWrites: false);
        await using (machine)
            for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await m.PostAsync("/ops/api/act", null)).StatusCode);
    }
}
