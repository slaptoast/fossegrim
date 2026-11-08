using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;

namespace Fossegrim.Api.Endpoints;

/// <summary>
/// The setup wizard's own endpoints. Everything else it configures goes
/// through the normal admin endpoints. Only status and creating the first
/// admin are anonymous, and the latter only works while there are no users.
/// </summary>
public static class Setup
{
    public static void MapSetupEndpoints(this WebApplication app)
    {
        var setupGroup = app.MapGroup("/api/setup").WithTags("Setup");

        setupGroup.MapGet("/", async (SetupService setup) => Results.Ok(await setup.GetStatusAsync()))
            .WithName("GetSetupStatus")
            .WithOpenApi();

        setupGroup.MapPost("/admin", async (
            CreateFirstAdminRequest request,
            HttpRequest http,
            FossegrimDbContext db,
            SetupService setup,
            SessionService sessions) =>
        {
            if (!await setup.IsSetupRequiredAsync())
            {
                return Results.Problem("Setup has already been done; sign in as an admin to change settings.", statusCode: StatusCodes.Status403Forbidden);
            }

            if (await AccountRules.CheckDisplayNameAsync(db, request.DisplayName) is { } nameError)
            {
                return Results.BadRequest(new IdentityErrorsResponse([nameError]));
            }

            var (user, result) = await setup.CreateFirstAdminAsync(request);
            if (user is null || !result.Succeeded)
            {
                return Results.BadRequest(new IdentityErrorsResponse(result.Errors.Select(e => e.Description).ToList()));
            }

            return Results.Ok(await sessions.SignInAsync(user, request.ClientName, Auth.UserAgentOf(http)));
        })
        .RequireRateLimiting(SignInLimits.Policy)
        .WithName("CreateFirstAdmin")
        .WithOpenApi();

        setupGroup.MapPost("/complete", async (SetupService setup) =>
        {
            await setup.MarkCompleteAsync();
            return Results.Ok(await setup.GetStatusAsync());
        })
        .RequireAuthorization(policy => policy.RequireRole("Admin"))
        .WithName("CompleteSetup")
        .WithOpenApi();

        var adminGroup = app.MapGroup("/api/admin")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole("Admin"));

        adminGroup.MapPut("/registration", async (RegistrationSettingsRequest request, SetupService setup) =>
        {
            await setup.SetRegistrationOpenAsync(request.Open);
            return Results.Ok(await setup.GetStatusAsync());
        })
        .WithName("UpdateRegistrationSettings")
        .WithOpenApi();

        adminGroup.MapPost("/users", async (CreateUserRequest request, FossegrimDbContext db, SetupService setup) =>
        {
            if (await AccountRules.CheckDisplayNameAsync(db, request.DisplayName) is { } nameError)
            {
                return Results.BadRequest(new IdentityErrorsResponse([nameError]));
            }

            var (user, result) = await setup.CreateUserAsync(request.Email, request.DisplayName, request.Password, request.IsAdmin);
            if (user is null || !result.Succeeded)
            {
                return Results.BadRequest(new IdentityErrorsResponse(result.Errors.Select(e => e.Description).ToList()));
            }

            return Results.Created($"/api/users/{user.Id}", new CreatedUserDto(user.Id, user.Email!, user.DisplayName ?? "", request.IsAdmin));
        })
        .WithName("CreateUser")
        .WithOpenApi();
    }
}
