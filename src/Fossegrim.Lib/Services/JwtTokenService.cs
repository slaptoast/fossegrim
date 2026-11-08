using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Fossegrim.Lib.Models;
using Microsoft.IdentityModel.Tokens;

namespace Fossegrim.Lib.Services;

public static class JwtTokenService
{
    // The UserSession a token belongs to. Not "sid", which the bearer
    // handler would remap to ClaimTypes.Sid.
    public const string SessionClaim = "fsg_sid";

    /// <summary>The audience of media tokens, which only /stream and /cover accept.</summary>
    public static string MediaAudience(string audience) => audience + ":media";

    /// <summary>
    /// A token for /stream and /cover URLs only (see Fossegrim.Api's
    /// MediaAuth): it names the user and session and nothing else, and its
    /// audience keeps it off the rest of the API.
    /// </summary>
    public static (string Token, DateTime ExpiresAt) GenerateMediaToken(
        string userId,
        string issuer,
        string audience,
        string signingKey,
        int expiryMinutes,
        Guid sessionId)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: MediaAudience(audience),
            claims: [new(JwtRegisteredClaimNames.Sub, userId), new(SessionClaim, sessionId.ToString())],
            expires: expiresAt,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public static (string Token, DateTime ExpiresAt) GenerateToken(
        ApplicationUser user,
        IEnumerable<string> roles,
        string issuer,
        string audience,
        string signingKey,
        int expiryMinutes,
        Guid sessionId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var expiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new(SessionClaim, sessionId.ToString()),
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
