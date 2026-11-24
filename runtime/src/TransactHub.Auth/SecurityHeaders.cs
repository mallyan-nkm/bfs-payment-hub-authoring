using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace TransactHub.Auth;

/// <summary>
/// Security headers on every answer of a runtime host (spec R-09-60): no sniffing, no framing, no referrer, and a content
/// security policy. The console's page (HTML) may load only its own scripts and styles, and talk only to its own APIs and the
/// bank's identity provider (<c>Auth:Oidc:Authority</c>, else the issuer), which the browser sign-in needs; every other answer
/// is data and gets a policy that allows nothing. HSTS is sent on an HTTPS request. A call that changes something (POST, PUT,
/// PATCH, DELETE) on a console's host, where people act, is limited per client address, <c>RateLimits:WritesPerMinute</c>
/// (600 by default, 0 for none): one caller cannot hold a host's actions, installs or restores (R-09-60). The machine hosts (the
/// release API's front-door calls, the native host) are not limited here: their volume is the bank's payments.
/// The console's page is drawn with Fluent UI (docs/adr/0003-ui-fluent.md), which writes its styles into style elements at run
/// time: the page served by <see cref="WriteConsolePageAsync"/> names a nonce, new on every answer, and the policy allows a
/// style element that carries it and no other inline style. Scripts stay the page's own files only.
/// </summary>
public static partial class SecurityHeaders
{
    const string NonceKey = "th.style-nonce";

    public static string PageCsp(string? identityProvider, string? styleNonce = null) =>
        $"default-src 'self'; script-src 'self'; style-src 'self'{(styleNonce is { Length: > 0 } n ? $" 'nonce-{n}'" : "")}; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'{(identityProvider is { Length: > 0 } idp ? " " + idp : "")}; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

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
                h["Content-Security-Policy"] = !html ? DataCsp : ctx.Items.TryGetValue(NonceKey, out var n) && n is string nonce ? PageCsp(IdentityProviderOrigin(cfg), nonce) : page;
                if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
                if (!html && !h.ContainsKey("Cache-Control") && ctx.Request.Path.Value?.Contains("/api", StringComparison.Ordinal) == true) h["Cache-Control"] = "no-store";
                return Task.CompletedTask;
            });
            await next();
        });
    }

    /// <summary>
    /// Serves the console's page (its <c>index.html</c>) with a style nonce for this answer: the page's
    /// <c>&lt;meta name="th-style-nonce"&gt;</c> carries it, and the policy names it.
    /// </summary>
    public static async Task WriteConsolePageAsync(HttpContext ctx, IFileInfo page)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        ctx.Items[NonceKey] = nonce;
        string html;
        using (var r = new StreamReader(page.CreateReadStream())) html = await r.ReadToEndAsync();
        html = NonceMeta().Replace(html, $"<meta name=\"th-style-nonce\" content=\"{nonce}\" />", 1);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.WriteAsync(html);
    }

    /// <summary>Answers <c>/</c> and <c>/index.html</c> with the console's page, before the static files would serve it as it is.</summary>
    public static IApplicationBuilder UseConsolePage(this IApplicationBuilder app, IFileProvider files) =>
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value;
            if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)) && path is "/" or "/index.html" && files.GetFileInfo("index.html") is { Exists: true } page)
            {
                await WriteConsolePageAsync(ctx, page);
                return;
            }
            await next();
        });

    [GeneratedRegex("<meta name=\"th-style-nonce\" content=\"[^\"]*\"\\s*/?>")]
    private static partial Regex NonceMeta();
}
