using Fossegrim.Lib.Data;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

public static class MediaItems
{
    public static void MapMediaItemEndpoints(this WebApplication app)
    {
        var mediaItemGroup = app.MapGroup("/api/mediaitems")
            .WithTags("MediaItems");

        mediaItemGroup.MapGet("/", async (FossegrimDbContext db) =>
        {
            var mediaItems = await db.MediaItems
                .Include(m => m.Artists)
                .Include(m => m.Albums)
                .ToListAsync();
            return Results.Ok(mediaItems);
        })
        .WithName("GetAllMediaItems")
        .WithOpenApi();

        mediaItemGroup.MapGet("/{id:guid}", async (Guid id, FossegrimDbContext db) =>
        {
            var mediaItem = await db.MediaItems
                .Include(m => m.Artists)
                .Include(m => m.Albums)
                .FirstOrDefaultAsync(m => m.Id == id);

            return mediaItem is not null ? Results.Ok(mediaItem) : Results.NotFound();
        })
        .WithName("GetMediaItemById")
        .WithOpenApi();
    }
}
