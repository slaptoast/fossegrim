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
            // Sizes as of the last scan, rather than reading every file on disk.
            var trackCount = await db.MediaItems.CountAsync();
            var totalBytes = await db.MediaItems.SumAsync(m => m.FileSize ?? 0);

            return Results.Ok(new LibraryStatsDto(artistCount, albumCount, trackCount, totalBytes));
        })
        .WithName("GetLibraryStats")
        .WithOpenApi();
    }
}
