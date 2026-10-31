using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Api.Endpoints;

public static class Version
{
    public static void MapVersionEndpoints(this WebApplication app)
    {
        // Public: the footer that displays this loads before login.
        app.MapGet("/api/version", (AppVersion version) => Results.Ok(new VersionDto(version.Value)))
            .WithName("GetVersion")
            .WithOpenApi();

        // Admin: whether a newer release has been published to Docker Hub.
        var updatesGroup = app.MapGroup("/api/admin/updates")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole("Admin"));

        updatesGroup.MapGet("/", async (UpdateChecker checker) => Results.Ok(await checker.GetStatusAsync()))
            .WithName("GetUpdateStatus")
            .WithOpenApi();

        // Works even with automatic checks turned off; that setting only
        // stops the daily background check from contacting Docker Hub.
        updatesGroup.MapPost("/check", async (UpdateChecker checker, CancellationToken cancellationToken) =>
        {
            if (!checker.CanCheck)
            {
                return Results.Problem("This is a development build, so there's no release version to compare.", statusCode: StatusCodes.Status409Conflict);
            }

            await checker.CheckAsync(cancellationToken);
            return Results.Ok(await checker.GetStatusAsync());
        })
        .WithName("CheckForUpdates")
        .WithOpenApi();

        updatesGroup.MapPut("/settings", async (UpdateCheckSettingsRequest request, UpdateChecker checker) =>
        {
            await checker.SetEnabledAsync(request.Enabled);
            return Results.Ok(await checker.GetStatusAsync());
        })
        .WithName("UpdateUpdateCheckSettings")
        .WithOpenApi();
    }
}
