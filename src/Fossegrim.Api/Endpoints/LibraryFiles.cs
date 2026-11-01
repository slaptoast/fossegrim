using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Services.Library;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

/// <summary>
/// Admin: renaming files from the server's template, and the Library
/// health reports (ID3v1/ID3v2 disagreements, files not named as the
/// template says). Anything that moves or rewrites files takes
/// <see cref="LibraryLock"/>, so it can't overlap a scan.
/// </summary>
public static class LibraryFiles
{
    private const string BusyMessage = "A scan is running; try again when it's finished.";

    public static void MapLibraryFileEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole("Admin"));

        group.MapGet("/rename/template", async (FileRenamer renamer) =>
            {
                var template = await renamer.GetTemplateAsync();
                return Results.Ok(new RenameTemplateDto(template, FileRenamer.Example(template)));
            })
            .WithName("GetRenameTemplate")
            .WithOpenApi();

        group.MapPut("/rename/template", async (UpdateRenameTemplateRequest request, FileRenamer renamer) =>
            {
                if (FileNameTemplate.Validate(request.Template) is { } error)
                {
                    return Results.BadRequest(new ApiErrorResponse(error));
                }

                await renamer.SetTemplateAsync(request.Template);
                var template = await renamer.GetTemplateAsync();
                return Results.Ok(new RenameTemplateDto(template, FileRenamer.Example(template)));
            })
            .WithName("UpdateRenameTemplate")
            .WithOpenApi();

        group.MapPost("/rename/preview", async (RenameRequest request, FileRenamer renamer) =>
                Ids(request) is var (ids, error) && error is not null
                    ? Results.BadRequest(new ApiErrorResponse(error))
                    : Results.Ok(await renamer.PlanAsync(ids)))
            .WithName("PreviewRename")
            .WithOpenApi();

        group.MapPost("/rename", async (RenameRequest request, FileRenamer renamer, LibraryLock libraryLock) =>
            {
                var (ids, error) = Ids(request);
                if (error is not null)
                {
                    return Results.BadRequest(new ApiErrorResponse(error));
                }
                if (!libraryLock.TryEnter())
                {
                    return Results.Conflict(new ApiErrorResponse(BusyMessage));
                }

                try
                {
                    return Results.Ok(await renamer.ApplyAsync(ids));
                }
                finally
                {
                    libraryLock.Release();
                }
            })
            .WithName("Rename")
            .WithOpenApi();

        group.MapGet("/library-health", async (FossegrimDbContext db, FileRenamer renamer) =>
            {
                var plan = await renamer.PlanAsync(null);
                return Results.Ok(new LibraryHealthDto(
                    await db.MediaItems.CountAsync(m => m.TagVersionMismatch != null),
                    plan.Entries.Count(e => e.Status is RenameStatus.Rename or RenameStatus.Conflict),
                    plan.Entries.Count(e => e.Status == RenameStatus.NotInMediaFolder),
                    plan.Entries.Count(e => e.Status == RenameStatus.FileMissing)));
            })
            .WithName("GetLibraryHealth")
            .WithOpenApi();

        group.MapGet("/library-health/tag-versions", async (FossegrimDbContext db) =>
            {
                var items = await db.MediaItems.AsNoTracking()
                    .Where(m => m.TagVersionMismatch != null)
                    .OrderBy(m => m.ArtistName).ThenBy(m => m.Album).ThenBy(m => m.Track).ThenBy(m => m.FileLocation)
                    .Select(m => new { m.Id, m.Title, m.ArtistName, m.Album, m.FileLocation, m.TagVersionMismatch })
                    .ToListAsync();
                return Results.Ok(items.Select(m => new TagVersionMismatchDto(
                    m.Id, m.Title, m.ArtistName, m.Album, m.FileLocation!, m.TagVersionMismatch!.Split(','))));
            })
            .WithName("GetTagVersionMismatches")
            .WithOpenApi();

        // Rewrites each file's ID3v1 tag from its ID3v2 tag, which is what Fossegrim reads.
        group.MapPost("/library-health/tag-versions/fix", async (FixTagVersionsRequest request, FossegrimDbContext db, LibraryLock libraryLock) =>
            {
                if (!libraryLock.TryEnter())
                {
                    return Results.Conflict(new ApiErrorResponse(BusyMessage));
                }

                try
                {
                    var ids = request.MediaItemIds ?? [];
                    var items = await db.MediaItems.Where(m => ids.Contains(m.Id)).ToListAsync();
                    var errors = ids.Except(items.Select(i => i.Id)).Select(id => new MediaItemTagUpdateError(id, "Media item not found.")).ToList();
                    var fixedCount = 0;

                    foreach (var item in items)
                    {
                        try
                        {
                            TagReader.SyncId3v1FromId3v2(item.FileLocation!);
                            using (var file = TagLib.File.Create(item.FileLocation!))
                            {
                                item.TagVersionMismatch = TagReader.CompareId3Versions(file);
                            }
                            var info = new FileInfo(item.FileLocation!);
                            item.FileSize = info.Length;
                            item.FileModifiedAt = info.LastWriteTimeUtc;
                            fixedCount++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add(new MediaItemTagUpdateError(item.Id, $"Couldn't rewrite the ID3v1 tag: {ex.Message}"));
                        }
                    }

                    await db.SaveChangesAsync();
                    return Results.Ok(new FixTagVersionsResultDto(fixedCount, errors));
                }
                finally
                {
                    libraryLock.Release();
                }
            })
            .WithName("FixTagVersionMismatches")
            .WithOpenApi();
    }

    private static (IReadOnlyCollection<Guid>? Ids, string? Error) Ids(RenameRequest request) =>
        request.All ? (null, null)
        : request.MediaItemIds is { Count: > 0 } ids ? (ids, null)
        : (null, "Choose some tracks, or the whole library.");
}
