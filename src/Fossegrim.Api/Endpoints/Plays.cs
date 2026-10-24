using System.Security.Claims;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

public static class Plays
{
    private const int DefaultHistoryLimit = 50;
    private const int MaxHistoryLimit = 500;

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
            FossegrimDbContext db) =>
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

            db.PlayEvents.Add(playEvent);
            await db.SaveChangesAsync();

            return Results.Created($"/api/plays/{playEvent.Id}", ToPlayEventDto(playEvent));
        })
        .WithName("RecordPlay")
        .WithOpenApi();

        // The calling user's listening history, newest first.
        playGroup.MapGet("/", async (
            int? limit,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var take = Math.Clamp(limit ?? DefaultHistoryLimit, 1, MaxHistoryLimit);

            var plays = await db.PlayEvents
                .Include(pe => pe.MediaItem)
                .Where(pe => pe.UserId == user.Id)
                .OrderByDescending(pe => pe.PlayedAt)
                .Take(take)
                .ToListAsync();

            return Results.Ok(plays.Select(ToPlayEventDto));
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

    private static PlayEventDto ToPlayEventDto(PlayEvent playEvent) => new(
        playEvent.Id,
        playEvent.PlayedAt,
        playEvent.ListenedFor,
        new PlaylistTrackDto(
            playEvent.MediaItemId,
            playEvent.MediaItem?.Title,
            playEvent.MediaItem?.ArtistName,
            playEvent.MediaItem?.Album,
            playEvent.MediaItem?.Duration));
}
