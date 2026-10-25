using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Fossegrim.Lib.Models;
using Microsoft.IdentityModel.Tokens;

namespace Fossegrim.Lib.Services;

public static class JwtTokenService
{
    public static (string Token, DateTime ExpiresAt) GenerateToken(
        ApplicationUser user,
        IEnumerable<string> roles,
        string issuer,
        string audience,
        string signingKey,
        int expiryMinutes)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var expiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
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
