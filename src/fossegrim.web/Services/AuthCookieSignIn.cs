using System.Security.Claims;
using Fossegrim.Lib.Dtos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Fossegrim.Web.Services;

/// <summary>
/// Builds Web's auth cookie from an Api login/register response. The cookie
/// carries the Api-issued JWT as a stored token so FossegrimApiClient can
/// forward it on every call - Web itself never mints or validates tokens.
/// </summary>
public static class AuthCookieSignIn
{
    public static async Task SignInAsync(HttpContext httpContext, LoginResponse response, bool isPersistent)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, response.UserId),
            new(ClaimTypes.Name, response.UserName),
            new(ClaimTypes.Email, response.Email),
        };
        claims.AddRange(response.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

        var properties = new AuthenticationProperties { IsPersistent = isPersistent };
        properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = response.Token }]);

        await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
    }
}
