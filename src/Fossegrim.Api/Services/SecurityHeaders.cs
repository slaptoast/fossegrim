namespace Fossegrim.Api.Services;

/// <summary>
/// Browser hardening headers on every response. The content security policy
/// allows what the Blazor WebAssembly app needs and nothing else: its own
/// scripts (plus compiling WebAssembly), inline styles (Blazor and Gridstack
/// set style attributes), and images from anywhere over HTTPS (cover search
/// results are shown straight from the providers). Tokens live in
/// localStorage, so keeping injected scripts out matters.
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob: https:; " +
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
