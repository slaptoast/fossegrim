using Fossegrim.Lib.Data;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

public static class Artists
{
    public static void MapArtistEndpoints(this WebApplication app)
    {
        var artistGroup = app.MapGroup("/api/artists")
            .WithTags("Artists");

        artistGroup.MapGet("/", async (FossegrimDbContext db) =>
        {
            var artists = await db.Artists
                .Include(a => a.MediaItems)
                .Include(a => a.Albums)
                .ToListAsync();
            return Results.Ok(artists);
        })
        .WithName("GetAllArtists")
        .WithOpenApi();

        artistGroup.MapGet("/{id:guid}", async (Guid id, FossegrimDbContext db) =>
        {
            var artist = await db.Artists
                .Include(a => a.MediaItems)
                .Include(a => a.Albums)
                .FirstOrDefaultAsync(a => a.Id == id);

            return artist is not null ? Results.Ok(artist) : Results.NotFound();
        })
        .WithName("GetArtistById")
        .WithOpenApi();
    }
}
