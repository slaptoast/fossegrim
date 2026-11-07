using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Services;
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
                    al.MediaItems.Select(m => new MediaItemSummaryDto(m.Id, m.Title, m.Duration, m.Track, m.Disc)),
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
                    al.MediaItems.Select(m => new MediaItemSummaryDto(m.Id, m.Title, m.Duration, m.Track, m.Disc)),
                    al.Artists.Select(a => new ArtistSummaryDto(a.Id, a.Name))))
                .FirstOrDefaultAsync();

            return album is not null ? Results.Ok(album) : Results.NotFound();
        })
        .WithName("GetAlbumById")
        .WithOpenApi();

        // The album's tracks in play order: by disc, track number, then title.
        albumGroup.MapGet("/{id:guid}/tracks", async (Guid id, FossegrimDbContext db) =>
        {
            if (!await db.Albums.AnyAsync(al => al.Id == id))
            {
                return Results.NotFound();
            }

            var tracks = await db.MediaItems
                .Where(m => m.Albums.Any(al => al.Id == id))
                .Select(MediaItemMapping.ToDto)
                .ToListAsync();
            return Results.Ok(tracks.InPlayOrder());
        })
        .WithName("GetAlbumTracks")
        .WithOpenApi();
    }
}
