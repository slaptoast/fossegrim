using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Fossegrim.Contracts.Dtos;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fossegrim.Web.Services;

/// <summary>
/// Derives Blazor's auth state directly from the stored JWT. JwtTokenService
/// (Fossegrim.Lib) writes claims using ClaimTypes.Role/Name and
/// JwtRegisteredClaimNames.Sub/Email, so JwtSecurityToken.Claims already
/// carries the exact claim types [Authorize(Roles = ...)] expects - no
/// remapping needed.
/// </summary>
public class JwtAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly TokenStore _tokenStore;

    public JwtAuthenticationStateProvider(TokenStore tokenStore)
    {
        _tokenStore = tokenStore;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await _tokenStore.GetTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            return Anonymous;
        }

        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(token))
        {
            return Anonymous;
        }

        var jwt = handler.ReadJwtToken(token);
        if (jwt.ValidTo < DateTime.UtcNow)
        {
            return Anonymous;
        }

        var identity = new ClaimsIdentity(jwt.Claims, authenticationType: "jwt");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public void MarkUserAsAuthenticated(LoginResponse response)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, response.UserId),
            new(JwtRegisteredClaimNames.Email, response.Email),
            new(ClaimTypes.Name, response.UserName),
        };
        claims.AddRange(response.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(claims, authenticationType: "jwt");
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity))));
    }

    public void MarkUserAsLoggedOut()
    {
        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous));
    }
}
