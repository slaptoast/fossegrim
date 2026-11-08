using System.Security.Claims;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
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

        // Wrong passwords count towards locking the account for a while
        // (SignInLimits); a locked account answers 429 even to the right one.
        authGroup.MapPost("/login", async (
            LoginRequest request,
            HttpRequest http,
            UserManager<ApplicationUser> userManager,
            IPasswordHasher<ApplicationUser> passwordHasher,
            SessionService sessions) =>
        {
            var user = string.IsNullOrWhiteSpace(request.Email) ? null : await userManager.FindByEmailAsync(request.Email);
            if (user is null)
            {
                SignInLimits.SpendTimeOnUnknownUser(passwordHasher, request.Password);
                return Results.Unauthorized();
            }

            if (await userManager.IsLockedOutAsync(user))
            {
                return LockedOut(user);
            }

            if (!await userManager.CheckPasswordAsync(user, request.Password ?? ""))
            {
                await userManager.AccessFailedAsync(user);
                return await userManager.IsLockedOutAsync(user) ? LockedOut(user) : Results.Unauthorized();
            }

            await userManager.ResetAccessFailedCountAsync(user);
            return Results.Ok(await sessions.SignInAsync(user, request.ClientName, UserAgentOf(http)));
        })
        .RequireRateLimiting(SignInLimits.Policy)
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
            FossegrimDbContext db,
            SetupService setup,
            SessionService sessions) =>
        {
            if (!await setup.IsRegistrationOpenAsync())
            {
                return Results.Problem("Registration is closed. Ask an admin for an account.", statusCode: StatusCodes.Status403Forbidden);
            }

            if (await AccountRules.CheckDisplayNameAsync(db, request.DisplayName) is { } nameError)
            {
                return Results.BadRequest(new IdentityErrorsResponse([nameError]));
            }

            var user = new ApplicationUser
            {
                UserName = request.Email,
                Email = request.Email,
                DisplayName = AccountRules.Clean(request.DisplayName),
                DateJoined = DateTime.UtcNow
            };

            var result = await userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                return Results.BadRequest(new IdentityErrorsResponse(result.Errors.Select(e => e.Description).ToList()));
            }

            return Results.Ok(await sessions.SignInAsync(user, request.ClientName, UserAgentOf(http)));
        })
        .RequireRateLimiting(SignInLimits.Policy)
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
        .RequireRateLimiting(SignInLimits.Policy)
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
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if ((await AccountRules.CheckDisplayNameAsync(db, request.DisplayName, user.Id) ?? AccountRules.CheckPhoneNumber(request.PhoneNumber)) is { } error)
            {
                return Results.BadRequest(new ApiErrorResponse(error));
            }

            await userManager.SetPhoneNumberAsync(user, string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim());
            user.DisplayName = AccountRules.Clean(request.DisplayName);
            await userManager.UpdateAsync(user);

            var phoneNumber = await userManager.GetPhoneNumberAsync(user);
            return Results.Ok(new ProfileDto(user.Id, user.UserName!, user.Email!, user.DisplayName, phoneNumber));
        })
        .RequireAuthorization()
        .WithName("UpdateProfile")
        .WithOpenApi();
    }

    private static IResult LockedOut(ApplicationUser user)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(((user.LockoutEnd ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalMinutes));
        return Results.Problem(
            $"Too many wrong passwords. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.",
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    internal static string? UserAgentOf(HttpRequest http) => http.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null;
}
