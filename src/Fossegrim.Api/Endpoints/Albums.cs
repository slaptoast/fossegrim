using System.Security.Claims;
using Fossegrim.Api.Services;
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

        // ?q= searches; sort= and order= see LibrarySorts.ForAlbums; page= and pageSize= (max 500).
        // listened=true leaves out what the caller has never played; favorites=true keeps only their favourites.
        albumGroup.MapGet("/", (string? q, string? sort, string? order, int? page, int? pageSize, bool? listened, bool? favorites, ClaimsPrincipal principal, LibraryBrowser browser, CancellationToken cancellationToken) =>
            Browse.ListAsync(q, sort, order, page, pageSize, listened ?? false, favorites ?? false, LibrarySorts.ForAlbums, principal, (request, userId) => browser.AlbumsAsync(request, userId, cancellationToken)))
        .WithName("GetAlbums");

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
        .WithName("GetAlbumById");

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
        .WithName("GetAlbumTracks");

        // The hover card. lookup=true fetches a missing or month-old Wikipedia summary first.
        albumGroup.MapGet("/{id:guid}/info", async (Guid id, bool? lookup, ClaimsPrincipal principal, LibraryInfoService info, CancellationToken cancellationToken) =>
            Browse.UserId(principal) is not { } userId ? Results.Unauthorized()
            : await info.AlbumAsync(id, userId, lookup ?? false, cancellationToken) is { } album ? Results.Ok(album)
            : Results.NotFound())
        .WithName("GetAlbumInfo");
    }
}
