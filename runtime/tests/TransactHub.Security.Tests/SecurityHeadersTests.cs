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
    public async Task The_console_page_allows_style_elements_only_by_a_nonce_new_on_every_answer_and_named_in_the_page()
    {
        // ADR 0003: Fluent UI writes its styles into style elements; the page names the nonce they carry. Scripts stay 'self'.
        var dir = Directory.CreateTempSubdirectory("th-console-page-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "index.html"), "<!doctype html><head><meta name=\"th-style-nonce\" content=\"\" /></head><title>console</title>");
            var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(dir.FullName);
            var b = WebApplication.CreateBuilder();
            b.WebHost.UseTestServer();
            var app = b.Build();
            app.UseRuntimeSecurityHeaders(app.Configuration);
            app.UseConsolePage(files);
            app.MapFallback(ctx => SecurityHeaders.WriteConsolePageAsync(ctx, files.GetFileInfo("index.html")));
            await app.StartAsync();
            await using var _ = app;
            var c = app.GetTestClient();
            static string Nonce(string csp) => System.Text.RegularExpressions.Regex.Match(csp, "style-src 'self' 'nonce-([^']+)'").Groups[1].Value;
            var answers = new List<(string csp, string html)>();
            foreach (var p in new[] { "/", "/index.html", "/payments/x" })
            {
                var r = await c.GetAsync(p);
                answers.Add((r.Headers.GetValues("Content-Security-Policy").Single(), await r.Content.ReadAsStringAsync()));
            }
            foreach (var (csp, html) in answers)
            {
                Assert.NotEqual("", Nonce(csp));
                Assert.Contains($"<meta name=\"th-style-nonce\" content=\"{Nonce(csp)}\" />", html);
                Assert.Contains("script-src 'self';", csp);
                Assert.DoesNotContain("unsafe-inline", csp);
            }
            Assert.Equal(3, answers.Select(a => Nonce(a.csp)).Distinct().Count());
        }
        finally { dir.Delete(true); }
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
