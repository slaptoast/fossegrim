using Fossegrim.Lib.Dtos;
using Fossegrim.Lib.Data;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

public static class Albums
{
    public static void MapAlbumEndpoints(this WebApplication app)
    {
        var albumGroup = app.MapGroup("/api/albums")
            .WithTags("Albums")
            .RequireAuthorization();

        albumGroup.MapGet("/", async (FossegrimDbContext db) =>
        {
            var albums = await db.Albums
                .Select(al => new AlbumDto(
                    al.Id,
                    al.Name,
                    al.Year,
                    al.Genre,
                    al.Label,
                    al.CoverImageUrl,
                    al.DateAdded,
                    al.LastModified,
                    al.MediaItems.Select(m => new MediaItemSummaryDto(m.Id, m.Title, m.Duration)),
                    al.Artists.Select(a => new ArtistSummaryDto(a.Id, a.Name))))
                .ToListAsync();
            return Results.Ok(albums);
        })
        .WithName("GetAllAlbums")
        .WithOpenApi();

        albumGroup.MapGet("/{id:guid}", async (Guid id, FossegrimDbContext db) =>
        {
            var album = await db.Albums
                .Where(al => al.Id == id)
                .Select(al => new AlbumDto(
                    al.Id,
                    al.Name,
                    al.Year,
                    al.Genre,
                    al.Label,
                    al.CoverImageUrl,
                    al.DateAdded,
                    al.LastModified,
                    al.MediaItems.Select(m => new MediaItemSummaryDto(m.Id, m.Title, m.Duration)),
                    al.Artists.Select(a => new ArtistSummaryDto(a.Id, a.Name))))
                .FirstOrDefaultAsync();

            return album is not null ? Results.Ok(album) : Results.NotFound();
        })
        .WithName("GetAlbumById")
        .WithOpenApi();
    }
}
