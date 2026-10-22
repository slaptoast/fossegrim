using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

public static class Library
{
    public static void MapLibraryEndpoints(this WebApplication app)
    {
        var libraryGroup = app.MapGroup("/api/library")
            .WithTags("Library")
            .RequireAuthorization();

        libraryGroup.MapGet("/stats", async (FossegrimDbContext db) =>
        {
            var artistCount = await db.Artists.CountAsync();
            var albumCount = await db.Albums.CountAsync();
            var fileLocations = await db.MediaItems
                .Select(m => m.FileLocation)
                .ToListAsync();

            // File size isn't stored in the database, so read it from disk.
            // Files that have moved or been deleted since the last scan are
            // skipped rather than failing the whole request.
            long totalBytes = 0;
            foreach (var location in fileLocations)
            {
                if (string.IsNullOrWhiteSpace(location))
                {
                    continue;
                }

                var file = new FileInfo(location);
                if (file.Exists)
                {
                    totalBytes += file.Length;
                }
            }

            return Results.Ok(new LibraryStatsDto(artistCount, albumCount, fileLocations.Count, totalBytes));
        })
        .WithName("GetLibraryStats")
        .WithOpenApi();
    }
}
