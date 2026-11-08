using Fossegrim.Lib.Services;

namespace Fossegrim.Api.Services;

/// <summary>
/// Browser hardening headers on every response. The content security policy
/// allows what the Blazor WebAssembly app needs and nothing else: its own
/// scripts (plus compiling WebAssembly), inline styles (Blazor and Gridstack
/// set style attributes, and custom themes can include CSS), and images from
/// the app and the cover providers only (cover search results are shown
/// straight from them). Tokens live in localStorage, so keeping injected
/// scripts out matters; keeping other servers out of img-src and font-src
/// means a theme's CSS can't send anything anywhere.
/// </summary>
public static class SecurityHeaders
{
    public static readonly string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        $"img-src 'self' data: blob: {string.Join(' ', CoverImages.BrowserImageSources)}; " +
        "media-src 'self' blob:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            // Stream and cover URLs carry a media token; don't pass them on.
            headers["Referrer-Policy"] = "no-referrer";
            return next(context);
        });
}
