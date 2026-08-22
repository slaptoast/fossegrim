using Fossegrim.Api.Dtos;
using Fossegrim.Lib.Data;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

public static class Artists
{
    public static void MapArtistEndpoints(this WebApplication app)
    {
        var artistGroup = app.MapGroup("/api/artists")
            .WithTags("Artists")
            .RequireAuthorization();

        artistGroup.MapGet("/", async (FossegrimDbContext db) =>
        {
            var artists = await db.Artists
                .Select(a => new ArtistDto(
                    a.Id,
                    a.Name,
                    a.Bio,
                    a.Country,
                    a.ImageUrl,
                    a.DateAdded,
                    a.LastModified,
                    a.MediaItems.Select(m => new MediaItemSummaryDto(m.Id, m.Title, m.Duration)),
                    a.Albums.Select(al => new AlbumSummaryDto(al.Id, al.Name, al.Year))))
                .ToListAsync();
            return Results.Ok(artists);
        })
        .WithName("GetAllArtists")
        .WithOpenApi();

        artistGroup.MapGet("/{id:guid}", async (Guid id, FossegrimDbContext db) =>
        {
            var artist = await db.Artists
                .Where(a => a.Id == id)
                .Select(a => new ArtistDto(
                    a.Id,
                    a.Name,
                    a.Bio,
                    a.Country,
                    a.ImageUrl,
                    a.DateAdded,
                    a.LastModified,
                    a.MediaItems.Select(m => new MediaItemSummaryDto(m.Id, m.Title, m.Duration)),
                    a.Albums.Select(al => new AlbumSummaryDto(al.Id, al.Name, al.Year))))
                .FirstOrDefaultAsync();

            return artist is not null ? Results.Ok(artist) : Results.NotFound();
        })
        .WithName("GetArtistById")
        .WithOpenApi();
    }
}
