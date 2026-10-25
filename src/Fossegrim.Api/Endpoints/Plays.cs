using System.Linq.Expressions;
using System.Security.Claims;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Enums;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

public static class Plays
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 500;
    private const int MaxSearchTerms = 10;
    private const string LikeEscape = "\\";

    // Tolerance for client clocks running ahead of the server's.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public static void MapPlayEndpoints(this WebApplication app)
    {
        var playGroup = app.MapGroup("/api/plays")
            .WithTags("Plays")
            .RequireAuthorization();

        // Records one play of a track for the calling user.
        playGroup.MapPost("/", async (
            RecordPlayRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db,
            Scrobbler scrobbler,
            ScrobbleSignal scrobbleSignal) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var now = DateTime.UtcNow;
            var playedAt = request.StartedAt?.UtcDateTime ?? now;
            if (ValidateRecordPlay(request, playedAt, now) is { } error)
            {
                return Results.BadRequest(error);
            }

            var mediaItem = await db.MediaItems.FindAsync(request.MediaItemId);
            if (mediaItem is null)
            {
                return Results.NotFound();
            }

            var playEvent = new PlayEvent
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                MediaItemId = mediaItem.Id,
                PlayedAt = playedAt,
                ListenedFor = request.ListenedFor,
                RecordedAt = now
            };

            await scrobbler.MarkForScrobblingAsync(playEvent, mediaItem, user);

            db.PlayEvents.Add(playEvent);
            await db.SaveChangesAsync();

            if (playEvent.ScrobbleStatus == ScrobbleStatus.Pending)
            {
                scrobbleSignal.Notify();
            }

            return Results.Created($"/api/plays/{playEvent.Id}", ToPlayEventDto(playEvent));
        })
        .WithName("RecordPlay")
        .WithOpenApi();

        // The calling user's listening history, newest first, a page at a
        // time. q narrows it to plays whose track matches every word in it,
        // each word against any of the track's tags (see MatchesTerm).
        playGroup.MapGet("/", async (
            string? q,
            int? page,
            int? pageSize,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
            var pageNumber = Math.Max(page ?? 1, 1);

            var query = db.PlayEvents.Where(pe => pe.UserId == user.Id);
            foreach (var term in SearchTerms(q))
            {
                query = query.Where(MatchesTerm(term));
            }

            var totalCount = await query.CountAsync();
            var plays = await query
                .OrderByDescending(pe => pe.PlayedAt)
                .ThenByDescending(pe => pe.Id)
                .Skip((pageNumber - 1) * size)
                .Take(size)
                .Include(pe => pe.MediaItem)
                .ToListAsync();

            return Results.Ok(new PlayHistoryPageDto(plays.Select(ToPlayEventDto).ToList(), totalCount, pageNumber, size));
        })
        .WithName("GetPlayHistory")
        .WithOpenApi();

        // One of the calling user's plays (the Location returned by POST).
        playGroup.MapGet("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var play = await db.PlayEvents
                .Include(pe => pe.MediaItem)
                .FirstOrDefaultAsync(pe => pe.Id == id && pe.UserId == user.Id);

            return play is null ? Results.NotFound() : Results.Ok(ToPlayEventDto(play));
        })
        .WithName("GetPlay")
        .WithOpenApi();
    }

    private static string? ValidateRecordPlay(RecordPlayRequest request, DateTime playedAt, DateTime now)
    {
        if (request.MediaItemId == Guid.Empty)
        {
            return "mediaItemId is required.";
        }

        if (playedAt > now + ClockSkew)
        {
            return "startedAt is in the future.";
        }

        if (request.ListenedFor is { } listenedFor)
        {
            if (listenedFor < TimeSpan.Zero)
            {
                return "listenedFor can't be negative.";
            }

            // Can't have heard more than the time since the track started.
            if (playedAt + listenedFor > now + ClockSkew)
            {
                return "listenedFor is longer than the time since startedAt.";
            }
        }

        return null;
    }

    private static IEnumerable<string> SearchTerms(string? q) =>
        (q ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSearchTerms);

    // A play matches a term when any of its track's tags contains it: title,
    // artist, album, genre and year on the track, plus the names of its
    // linked artists and albums and those albums' label and genre. SQLite's
    // LIKE is case-insensitive for ASCII letters only.
    private static Expression<Func<PlayEvent, bool>> MatchesTerm(string term)
    {
        var pattern = $"%{EscapeLike(term)}%";
        return pe =>
            EF.Functions.Like(pe.MediaItem!.Title, pattern, LikeEscape) ||
            EF.Functions.Like(pe.MediaItem.ArtistName, pattern, LikeEscape) ||
            EF.Functions.Like(pe.MediaItem.Album, pattern, LikeEscape) ||
            EF.Functions.Like(pe.MediaItem.Genre, pattern, LikeEscape) ||
            EF.Functions.Like(pe.MediaItem.Year.ToString(), pattern, LikeEscape) ||
            pe.MediaItem.Artists.Any(a => EF.Functions.Like(a.Name, pattern, LikeEscape)) ||
            pe.MediaItem.Albums.Any(a =>
                EF.Functions.Like(a.Name, pattern, LikeEscape) ||
                EF.Functions.Like(a.Label, pattern, LikeEscape) ||
                EF.Functions.Like(a.Genre, pattern, LikeEscape));
    }

    // So a search for "100%" or "a_b" matches those characters literally.
    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static PlayEventDto ToPlayEventDto(PlayEvent playEvent) => new(
        playEvent.Id,
        playEvent.PlayedAt,
        playEvent.ListenedFor,
        playEvent.ScrobbleStatus?.ToString(),
        playEvent.ScrobbleError,
        new PlayedTrackDto(
            playEvent.MediaItemId,
            playEvent.MediaItem?.Title,
            playEvent.MediaItem?.ArtistName,
            playEvent.MediaItem?.Album,
            playEvent.MediaItem?.Year,
            playEvent.MediaItem?.Track,
            playEvent.MediaItem?.Genre,
            playEvent.MediaItem?.Duration));
}
