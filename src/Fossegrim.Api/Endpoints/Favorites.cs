using System.Security.Claims;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

/// <summary>
/// The caller's favourite tracks, albums and artists (Subsonic's star and
/// unstar). Adding and removing are idempotent. To list favourites, use the
/// list endpoints with favorites=true.
/// </summary>
public static class Favorites
{
    public static void MapFavoriteEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/favorites")
            .WithTags("Favorites")
            .RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal principal, FossegrimDbContext db) =>
        {
            if (Browse.UserId(principal) is not { } userId)
            {
                return Results.Unauthorized();
            }
            return Results.Ok(new FavoritesDto(
                await db.FavoriteTracks.Where(f => f.UserId == userId).Select(f => f.MediaItemId).ToListAsync(),
                await db.FavoriteAlbums.Where(f => f.UserId == userId).Select(f => f.AlbumId).ToListAsync(),
                await db.FavoriteArtists.Where(f => f.UserId == userId).Select(f => f.ArtistId).ToListAsync()));
        })
        .WithName("GetFavorites");

        group.MapPut("/{kind}/{id:guid}", async (string kind, Guid id, ClaimsPrincipal principal, FossegrimDbContext db, TimeProvider time) =>
            Browse.UserId(principal) is not { } userId ? Results.Unauthorized()
            : await SetAsync(db, userId, kind, id, favorite: true, time.GetUtcNow().UtcDateTime) switch
            {
                FavoriteChange.Done => Results.NoContent(),
                FavoriteChange.NotFound => Results.NotFound(),
                _ => UnknownKind(),
            })
        .WithName("AddFavorite");

        group.MapDelete("/{kind}/{id:guid}", async (string kind, Guid id, ClaimsPrincipal principal, FossegrimDbContext db, TimeProvider time) =>
            Browse.UserId(principal) is not { } userId ? Results.Unauthorized()
            : await SetAsync(db, userId, kind, id, favorite: false, time.GetUtcNow().UtcDateTime) == FavoriteChange.UnknownKind ? UnknownKind()
            : Results.NoContent())
        .WithName("RemoveFavorite");
    }

    internal enum FavoriteChange { Done, NotFound, UnknownKind }

    /// <summary>
    /// Makes something a favourite of the user's, or not. Idempotent.
    /// Removing never reports NotFound: what isn't there is already removed.
    /// </summary>
    internal static async Task<FavoriteChange> SetAsync(FossegrimDbContext db, string userId, string kind, Guid id, bool favorite, DateTime now)
    {
        if (!favorite)
        {
            switch (kind)
            {
                case FavoriteKinds.Tracks:
                    await db.FavoriteTracks.Where(f => f.UserId == userId && f.MediaItemId == id).ExecuteDeleteAsync();
                    break;
                case FavoriteKinds.Albums:
                    await db.FavoriteAlbums.Where(f => f.UserId == userId && f.AlbumId == id).ExecuteDeleteAsync();
                    break;
                case FavoriteKinds.Artists:
                    await db.FavoriteArtists.Where(f => f.UserId == userId && f.ArtistId == id).ExecuteDeleteAsync();
                    break;
                default:
                    return FavoriteChange.UnknownKind;
            }
            return FavoriteChange.Done;
        }

        switch (kind)
        {
            case FavoriteKinds.Tracks when await db.MediaItems.AnyAsync(m => m.Id == id):
                if (!await db.FavoriteTracks.AnyAsync(f => f.UserId == userId && f.MediaItemId == id))
                {
                    db.FavoriteTracks.Add(new FavoriteTrack { UserId = userId, MediaItemId = id, CreatedAt = now });
                }
                break;
            case FavoriteKinds.Albums when await db.Albums.AnyAsync(a => a.Id == id):
                if (!await db.FavoriteAlbums.AnyAsync(f => f.UserId == userId && f.AlbumId == id))
                {
                    db.FavoriteAlbums.Add(new FavoriteAlbum { UserId = userId, AlbumId = id, CreatedAt = now });
                }
                break;
            case FavoriteKinds.Artists when await db.Artists.AnyAsync(a => a.Id == id):
                if (!await db.FavoriteArtists.AnyAsync(f => f.UserId == userId && f.ArtistId == id))
                {
                    db.FavoriteArtists.Add(new FavoriteArtist { UserId = userId, ArtistId = id, CreatedAt = now });
                }
                break;
            case FavoriteKinds.Tracks or FavoriteKinds.Albums or FavoriteKinds.Artists:
                return FavoriteChange.NotFound;
            default:
                return FavoriteChange.UnknownKind;
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Added by a request that ran at the same moment: already a favourite.
        }
        return FavoriteChange.Done;
    }

    private static IResult UnknownKind() =>
        Results.BadRequest(new ApiErrorResponse($"kind must be {FavoriteKinds.Tracks}, {FavoriteKinds.Albums} or {FavoriteKinds.Artists}."));
}
