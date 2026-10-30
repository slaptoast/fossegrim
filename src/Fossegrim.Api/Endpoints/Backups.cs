using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Api.Endpoints;

/// <summary>
/// Admin: backup schedule, run-now, and the backups in Backup:Directory.
/// File names are only accepted if they look like Fossegrim's own backups,
/// so these can't reach anything else in that folder.
/// </summary>
public static class Backups
{
    public static void MapBackupEndpoints(this WebApplication app)
    {
        var backupGroup = app.MapGroup("/api/admin/backups")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole("Admin"));

        backupGroup.MapGet("/", async (BackupService backups) => Results.Ok(await backups.GetStatusAsync()))
            .WithName("GetBackupStatus")
            .WithOpenApi();

        backupGroup.MapPut("/schedule", async (BackupScheduleDto schedule, BackupService backups) =>
        {
            if (BackupService.Validate(schedule) is { } error)
            {
                return Results.BadRequest(error);
            }

            await backups.SaveScheduleAsync(schedule);
            return Results.Ok(await backups.GetStatusAsync());
        })
        .WithName("UpdateBackupSchedule")
        .WithOpenApi();

        // Runs synchronously: even a large library's database backs up in
        // seconds, and the caller wants to know how it went.
        backupGroup.MapPost("/", async (BackupService backups, CancellationToken cancellationToken) =>
        {
            if (await backups.RunAsync(BackupService.ManualTrigger, cancellationToken) is null)
            {
                return Results.Problem("A backup is already running.", statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(await backups.GetStatusAsync());
        })
        .WithName("RunBackup")
        .WithOpenApi();

        backupGroup.MapGet("/files/{fileName}", (string fileName, BackupService backups) =>
            backups.FindBackup(fileName) is { } path
                ? Results.File(path, "application/zip", fileName)
                : Results.NotFound())
        .WithName("DownloadBackup")
        .WithOpenApi();

        backupGroup.MapDelete("/files/{fileName}", (string fileName, BackupService backups) =>
            backups.DeleteBackup(fileName) ? Results.NoContent() : Results.NotFound())
        .WithName("DeleteBackup")
        .WithOpenApi();

        // IANA ids on Linux and macOS, i.e. what a schedule's TimeZone accepts here.
        backupGroup.MapGet("/time-zones", () =>
            Results.Ok(TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Order(StringComparer.Ordinal).ToList()))
        .WithName("GetBackupTimeZones")
        .WithOpenApi();
    }
}
