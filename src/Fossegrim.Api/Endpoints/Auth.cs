using System.Security.Claims;
using Fossegrim.Contracts.Dtos;
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
            return Results.Ok(IssueLoginResponse(user, roles, configuration));
        })
        .WithName("Login")
        .WithOpenApi();

        authGroup.MapPost("/register", async (
            RegisterRequest request,
            UserManager<ApplicationUser> userManager,
            IConfiguration configuration) =>
        {
            var user = new ApplicationUser
            {
                UserName = request.Email,
                Email = request.Email,
                DisplayName = request.DisplayName,
                DateJoined = DateTime.UtcNow
            };

            var result = await userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                return Results.BadRequest(new IdentityErrorsResponse(result.Errors.Select(e => e.Description).ToList()));
            }

            var roles = await userManager.GetRolesAsync(user);
            return Results.Ok(IssueLoginResponse(user, roles, configuration));
        })
        .WithName("Register")
        .WithOpenApi();

        authGroup.MapPost("/change-password", async (
            ChangePasswordRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var result = await userManager.ChangePasswordAsync(user, request.OldPassword, request.NewPassword);
            if (!result.Succeeded)
            {
                return Results.BadRequest(new IdentityErrorsResponse(result.Errors.Select(e => e.Description).ToList()));
            }

            return Results.NoContent();
        })
        .RequireAuthorization()
        .WithName("ChangePassword")
        .WithOpenApi();

        authGroup.MapGet("/me", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var phoneNumber = await userManager.GetPhoneNumberAsync(user);
            return Results.Ok(new ProfileDto(user.Id, user.UserName!, user.Email!, user.DisplayName, phoneNumber));
        })
        .RequireAuthorization()
        .WithName("GetProfile")
        .WithOpenApi();

        authGroup.MapPut("/me", async (
            UpdateProfileRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            await userManager.SetPhoneNumberAsync(user, request.PhoneNumber);
            user.DisplayName = request.DisplayName;
            await userManager.UpdateAsync(user);

            var phoneNumber = await userManager.GetPhoneNumberAsync(user);
            return Results.Ok(new ProfileDto(user.Id, user.UserName!, user.Email!, user.DisplayName, phoneNumber));
        })
        .RequireAuthorization()
        .WithName("UpdateProfile")
        .WithOpenApi();
    }

    private static LoginResponse IssueLoginResponse(ApplicationUser user, IList<string> roles, IConfiguration configuration)
    {
        var jwtSection = configuration.GetSection("Jwt");
        var (token, expiresAt) = JwtTokenService.GenerateToken(
            user,
            roles,
            jwtSection["Issuer"]!,
            jwtSection["Audience"]!,
            jwtSection["Key"]!,
            jwtSection.GetValue<int>("ExpiryMinutes"));

        return new LoginResponse(token, expiresAt, user.Id, user.UserName!, user.Email!, user.DisplayName, roles.ToList());
    }
}
