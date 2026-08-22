using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Microsoft.AspNetCore.Identity;

namespace Fossegrim.Api.Endpoints;

public static class Auth
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var authGroup = app.MapGroup("/api/auth")
            .WithTags("Auth");

        authGroup.MapPost("/login", async (
            LoginRequest request,
            UserManager<ApplicationUser> userManager,
            IConfiguration configuration) =>
        {
            var user = await userManager.FindByEmailAsync(request.Email);
            if (user is null || !await userManager.CheckPasswordAsync(user, request.Password))
            {
                return Results.Unauthorized();
            }

            var roles = await userManager.GetRolesAsync(user);
            var jwtSection = configuration.GetSection("Jwt");
            var (token, expiresAt) = JwtTokenService.GenerateToken(
                user,
                roles,
                jwtSection["Issuer"]!,
                jwtSection["Audience"]!,
                jwtSection["Key"]!,
                jwtSection.GetValue<int>("ExpiryMinutes"));

            return Results.Ok(new LoginResponse(token, expiresAt));
        })
        .WithName("Login")
        .WithOpenApi();
    }
}

public record LoginRequest(string Email, string Password);

public record LoginResponse(string Token, DateTime ExpiresAt);
