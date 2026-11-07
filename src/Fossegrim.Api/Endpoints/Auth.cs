using System.Security.Claims;
using Fossegrim.Api.Services;
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
            HttpRequest http,
            UserManager<ApplicationUser> userManager,
            SessionService sessions) =>
        {
            var user = await userManager.FindByEmailAsync(request.Email);
            if (user is null || !await userManager.CheckPasswordAsync(user, request.Password))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await sessions.SignInAsync(user, request.ClientName, UserAgentOf(http)));
        })
        .WithName("Login")
        .WithOpenApi();

        // Swaps a refresh token for a new access token and refresh token.
        authGroup.MapPost("/refresh", async (RefreshRequest request, SessionService sessions) =>
            await sessions.RefreshAsync(request.RefreshToken) is { } response
                ? Results.Ok(response)
                : Results.Unauthorized())
        .WithName("RefreshSession")
        .WithOpenApi();

        // Signs out the device making the request.
        authGroup.MapPost("/logout", async (ClaimsPrincipal principal, UserManager<ApplicationUser> userManager, SessionService sessions) =>
        {
            if (userManager.GetUserId(principal) is { } userId && SessionService.CurrentSessionId(principal) is { } sessionId)
            {
                await sessions.RevokeAsync(userId, sessionId);
            }
            return Results.NoContent();
        })
        .RequireAuthorization()
        .WithName("Logout")
        .WithOpenApi();

        authGroup.MapGet("/sessions", async (ClaimsPrincipal principal, UserManager<ApplicationUser> userManager, SessionService sessions) =>
            userManager.GetUserId(principal) is { } userId
                ? Results.Ok(await sessions.ListAsync(userId, SessionService.CurrentSessionId(principal)))
                : Results.Unauthorized())
        .RequireAuthorization()
        .WithName("GetSessions")
        .WithOpenApi();

        authGroup.MapDelete("/sessions/{id:guid}", async (Guid id, ClaimsPrincipal principal, UserManager<ApplicationUser> userManager, SessionService sessions) =>
            userManager.GetUserId(principal) is { } userId
                ? await sessions.RevokeAsync(userId, id) ? Results.NoContent() : Results.NotFound()
                : Results.Unauthorized())
        .RequireAuthorization()
        .WithName("RevokeSession")
        .WithOpenApi();

        // Signs out every other device.
        authGroup.MapDelete("/sessions", async (ClaimsPrincipal principal, UserManager<ApplicationUser> userManager, SessionService sessions) =>
        {
            if (userManager.GetUserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }
            await sessions.RevokeOthersAsync(userId, SessionService.CurrentSessionId(principal));
            return Results.NoContent();
        })
        .RequireAuthorization()
        .WithName("RevokeOtherSessions")
        .WithOpenApi();

        authGroup.MapPost("/register", async (
            RegisterRequest request,
            HttpRequest http,
            UserManager<ApplicationUser> userManager,
            SetupService setup,
            SessionService sessions) =>
        {
            if (!await setup.IsRegistrationOpenAsync())
            {
                return Results.Problem("Registration is closed. Ask an admin for an account.", statusCode: StatusCodes.Status403Forbidden);
            }

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

            return Results.Ok(await sessions.SignInAsync(user, request.ClientName, UserAgentOf(http)));
        })
        .WithName("Register")
        .WithOpenApi();

        // Also signs out the user's other devices.
        authGroup.MapPost("/change-password", async (
            ChangePasswordRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            SessionService sessions) =>
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

            await sessions.RevokeOthersAsync(user.Id, SessionService.CurrentSessionId(principal));
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

    internal static string? UserAgentOf(HttpRequest http) => http.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null;
}
