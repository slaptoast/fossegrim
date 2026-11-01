using System.Security.Claims;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

/// <summary>
/// Random tracks and albums. mode=full ignores listening history;
/// mode=adjusted (the default) favours what the calling user hasn't played
/// recently, or ever, without excluding anything (see RandomPicker).
/// </summary>
public static class RandomPicks
{
    public const int DefaultCount = 5;
    public const int MaxCount = 50;

    public static void MapRandomEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/random")
            .WithTags("Random")
            .RequireAuthorization();

        group.MapGet("/tracks", (string? mode, int? count, ClaimsPrincipal principal, UserManager<ApplicationUser> userManager, FossegrimDbContext db, TimeProvider time) =>
                RandomTracksAsync(new RandomPickRequest(mode, count, null), principal, userManager, db, time))
            .WithName("GetRandomTracks")
            .WithOpenApi();

        // The same, leaving out ExcludeIds (e.g. tracks already in the queue).
        // If that leaves nothing, the whole library is a candidate again.
        group.MapPost("/tracks", RandomTracksAsync)
            .WithName("PickRandomTracks")
            .WithOpenApi();

        group.MapGet("/albums", (string? mode, int? count, ClaimsPrincipal principal, UserManager<ApplicationUser> userManager, FossegrimDbContext db, TimeProvider time) =>
                RandomAlbumsAsync(new RandomPickRequest(mode, count, null), principal, userManager, db, time))
            .WithName("GetRandomAlbums")
            .WithOpenApi();

        // The same, leaving out ExcludeIds (e.g. albums already played this
        // session). If that leaves nothing, every album is a candidate again.
        group.MapPost("/albums", RandomAlbumsAsync)
            .WithName("PickRandomAlbums")
            .WithOpenApi();
    }

    private static async Task<IResult> RandomTracksAsync(
        RandomPickRequest request,
        ClaimsPrincipal principal,
        UserManager<ApplicationUser> userManager,
        FossegrimDbContext db,
        TimeProvider time)
    {
        if (ParseMode(request.Mode) is not { } adjusted)
        {
            return Results.BadRequest("mode must be \"full\" or \"adjusted\".");
        }

        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var trackIds = WithoutExcluded(await db.MediaItems.Select(m => m.Id).ToListAsync(), request.ExcludeIds);
        var lastPlayed = adjusted ? await LastPlayedAsync(db, user.Id) : [];
        var now = time.GetUtcNow().UtcDateTime;
        var picked = RandomPicker.Pick(
            trackIds.Select(id => (id, adjusted ? RandomPicker.Weight(lastPlayed.GetValueOrDefault(id), now) : 1)),
            ClampCount(request.Count),
            Random.Shared);

        var tracks = await db.MediaItems
            .Where(m => picked.Contains(m.Id))
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
        return Results.Ok(tracks.OrderBy(t => picked.IndexOf(t.Id)));
    }

    // An album counts as last played when any of its tracks was; albums
    // with no tracks are never picked.
    private static async Task<IResult> RandomAlbumsAsync(
        RandomPickRequest request,
        ClaimsPrincipal principal,
        UserManager<ApplicationUser> userManager,
        FossegrimDbContext db,
        TimeProvider time)
    {
        if (ParseMode(request.Mode) is not { } adjusted)
        {
            return Results.BadRequest("mode must be \"full\" or \"adjusted\".");
        }

        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var albums = await db.Albums
            .Where(al => al.MediaItems.Any())
            .Select(al => new { al.Id, TrackIds = al.MediaItems.Select(m => m.Id).ToList() })
            .ToListAsync();
        var candidates = WithoutExcluded(albums.Select(al => al.Id).ToList(), request.ExcludeIds).ToHashSet();
        var lastPlayed = adjusted ? await LastPlayedAsync(db, user.Id) : [];
        var now = time.GetUtcNow().UtcDateTime;
        var picked = RandomPicker.Pick(
            albums.Where(al => candidates.Contains(al.Id)).Select(al => (al.Id, adjusted
                ? RandomPicker.Weight(al.TrackIds.Select(id => lastPlayed.GetValueOrDefault(id)).Max(), now)
                : 1)),
            ClampCount(request.Count),
            Random.Shared);

        var result = await db.Albums
            .Where(al => picked.Contains(al.Id))
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
        return Results.Ok(result.OrderBy(al => picked.IndexOf(al.Id)));
    }

    // Leaving out everything would leave nothing to pick, so then nothing is left out.
    private static List<Guid> WithoutExcluded(List<Guid> ids, IEnumerable<Guid>? exclude)
    {
        var excluded = exclude?.ToHashSet() ?? [];
        var remaining = ids.Where(id => !excluded.Contains(id)).ToList();
        return remaining.Count > 0 ? remaining : ids;
    }

    // True for adjusted (also when omitted), false for full, null if unrecognised.
    private static bool? ParseMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        null or "" or "adjusted" => true,
        "full" => false,
        _ => null,
    };

    private static int ClampCount(int? count) => Math.Clamp(count ?? DefaultCount, 1, MaxCount);

    private static async Task<Dictionary<Guid, DateTime?>> LastPlayedAsync(FossegrimDbContext db, string userId) =>
        await db.PlayEvents
            .Where(p => p.UserId == userId)
            .GroupBy(p => p.MediaItemId)
            .Select(g => new { g.Key, Last = g.Max(p => p.PlayedAt) })
            .ToDictionaryAsync(x => x.Key, x => (DateTime?)x.Last);
}
