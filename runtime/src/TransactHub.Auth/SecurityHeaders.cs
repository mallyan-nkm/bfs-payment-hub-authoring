using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace TransactHub.Auth;

/// <summary>
/// Security headers on every answer of a runtime host (spec R-09-60): no sniffing, no framing, no referrer, and a content
/// security policy. The console's page (HTML) may load only its own scripts and styles, and talk only to its own APIs and the
/// bank's identity provider (<c>Auth:Oidc:Authority</c>, else the issuer), which the browser sign-in needs; every other answer
/// is data and gets a policy that allows nothing. HSTS is sent on an HTTPS request. A call that changes something (POST, PUT,
/// PATCH, DELETE) on a console's host, where people act, is limited per client address, <c>RateLimits:WritesPerMinute</c>
/// (600 by default, 0 for none): one caller cannot hold a host's actions, installs or restores (R-09-60). The machine hosts (the
/// release API's front-door calls, the native host) are not limited here: their volume is the bank's payments.
/// </summary>
public static class SecurityHeaders
{
    public static string PageCsp(string? identityProvider) =>
        $"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'{(identityProvider is { Length: > 0 } idp ? " " + idp : "")}; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public const string DataCsp = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";

    /// <summary>The origin of the bank's identity provider, from the host's configuration, or null (dev mode).</summary>
    public static string? IdentityProviderOrigin(IConfiguration cfg) =>
        (cfg["Auth:Oidc:Authority"] ?? cfg["Auth:Oidc:Issuer"]) is { Length: > 0 } a && Uri.TryCreate(a, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : null;

    /// <summary>Adds the headers to every response, before anything else answers.</summary>
    public static IApplicationBuilder UseRuntimeSecurityHeaders(this IApplicationBuilder app, IConfiguration cfg, bool limitWrites = true)
    {
        var page = PageCsp(IdentityProviderOrigin(cfg));
        var limit = !limitWrites ? 0 : int.TryParse(cfg["RateLimits:WritesPerMinute"], out var n) ? n : 600;
        var windows = new ConcurrentDictionary<string, (long start, int count)>(StringComparer.Ordinal);
        return app.Use(async (ctx, next) =>
        {
            if (limit > 0 && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method))
            {
                var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var now = Environment.TickCount64;
                if (windows.Count > 50_000) foreach (var k in windows.Where(w => now - w.Value.start >= 60_000).Select(w => w.Key).ToList()) windows.TryRemove(k, out _);
                var w = windows.AddOrUpdate(key, _ => (now, 1), (_, o) => now - o.start >= 60_000 ? (now, 1) : (o.start, o.count + 1));
                if (w.count > limit)
                {
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    ctx.Response.Headers.RetryAfter = "60";
                    await ctx.Response.WriteAsJsonAsync(new { error = $"More than {limit} changes a minute from this address: wait a minute (R-09-60)" });
                    return;
                }
            }
            ctx.Response.OnStarting(() =>
            {
                var h = ctx.Response.Headers;
                h["X-Content-Type-Options"] = "nosniff";
                h["X-Frame-Options"] = "DENY";
                h["Referrer-Policy"] = "no-referrer";
                h["Cross-Origin-Opener-Policy"] = "same-origin";
                h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
                var html = ctx.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;
                h["Content-Security-Policy"] = html ? page : DataCsp;
                if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
                if (!html && !h.ContainsKey("Cache-Control") && ctx.Request.Path.Value?.Contains("/api", StringComparison.Ordinal) == true) h["Cache-Control"] = "no-store";
                return Task.CompletedTask;
            });
            await next();
        });
    }
}
