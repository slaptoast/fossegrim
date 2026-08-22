using Fossegrim.Lib.Data;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

public static class Albums
{
    public static void MapAlbumEndpoints(this WebApplication app)
    {
        var albumGroup = app.MapGroup("/api/albums")
            .WithTags("Albums");

        albumGroup.MapGet("/", async (FossegrimDbContext db) =>
        {
            var albums = await db.Albums
                .Include(a => a.Artists)
                .Include(a => a.MediaItems)
                .ToListAsync();
            return Results.Ok(albums);
        })
        .WithName("GetAllAlbums")
        .WithOpenApi();

        albumGroup.MapGet("/{id:guid}", async (Guid id, FossegrimDbContext db) =>
        {
            var album = await db.Albums
                .Include(a => a.Artists)
                .Include(a => a.MediaItems)
                .FirstOrDefaultAsync(a => a.Id == id);

            return album is not null ? Results.Ok(album) : Results.NotFound();
        })
        .WithName("GetAlbumById")
        .WithOpenApi();
    }
}
