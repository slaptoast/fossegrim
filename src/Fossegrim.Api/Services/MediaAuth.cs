using Fossegrim.Lib.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Fossegrim.Api.Services;

/// <summary>
/// Authentication for /stream and /cover. Browsers load those from
/// &lt;audio&gt; and &lt;img&gt; URLs, which can't carry an Authorization
/// header, so they take a media token in ?access_token= instead (see
/// <see cref="JwtTokenService.GenerateMediaToken"/>). Media tokens only work
/// here, so one that ends up in a proxy log or browser history can't be
/// used on the rest of the API. Apps that can set headers may also send
/// their normal access token as a bearer token.
/// </summary>
public static class MediaAuth
{
    public const string Scheme = "Media";

    public static TBuilder RequireMediaAuthorization<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, Scheme)
            .RequireAuthenticatedUser());

    /// <summary>
    /// Rejects tokens whose session has been signed out, so signing a device
    /// out takes effect at once rather than when its tokens expire. Every
    /// token carries a session id.
    /// </summary>
    public static async Task RequireActiveSessionAsync(TokenValidatedContext context)
    {
        var sessions = context.HttpContext.RequestServices.GetRequiredService<SessionService>();
        if (context.Principal is null || SessionService.CurrentSessionId(context.Principal) is not { } sessionId || !await sessions.IsActiveAsync(sessionId))
        {
            context.Fail("This device has been signed out.");
        }
    }
}
