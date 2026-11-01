using Fossegrim.Contracts.Dtos;
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

        // The album's tracks in play order: by track number, then title.
        albumGroup.MapGet("/{id:guid}/tracks", async (Guid id, FossegrimDbContext db) =>
        {
            if (!await db.Albums.AnyAsync(al => al.Id == id))
            {
                return Results.NotFound();
            }

            var tracks = await db.MediaItems
                .Where(m => m.Albums.Any(al => al.Id == id))
                .Select(m => new MediaItemDto(
                    m.Id,
                    m.Title,
                    m.ArtistName,
                    m.Album,
                    m.DateAdded,
                    m.LastModified,
                    m.Year,
                    m.Track,
                    m.Genre,
                    m.Duration,
                    m.Bitrate,
                    m.Artists.Select(a => new ArtistSummaryDto(a.Id, a.Name)),
                    m.Albums.Select(al => new AlbumSummaryDto(al.Id, al.Name, al.Year))))
                .ToListAsync();
            return Results.Ok(tracks
                .OrderBy(m => m.Track ?? int.MaxValue)
                .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase));
        })
        .WithName("GetAlbumTracks")
        .WithOpenApi();
    }
}
