using System.Security.Claims;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

/// <summary>
/// Per-user Last.fm linking (web auth flow) and now-playing updates. The
/// session key stays on the server; clients only ever see the Last.fm
/// username. Scrobbling is driven by play history, not by these endpoints.
/// </summary>
public static class LastFm
{
    // Last.fm error 9: invalid session key, i.e. the user revoked access.
    private const int InvalidSessionKeyError = 9;

    public static void MapLastFmEndpoints(this WebApplication app)
    {
        var lastFmGroup = app.MapGroup("/api/lastfm")
            .WithTags("LastFm")
            .RequireAuthorization();

        lastFmGroup.MapGet("/", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            LastFmClient lastFm,
            LastFmAccountStore accounts) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var account = lastFm.IsConfigured ? await accounts.GetAsync(user) : null;
            return Results.Ok(new LastFmStatusDto(lastFm.IsConfigured, account is not null, account?.UserName));
        })
        .WithName("GetLastFmStatus")
        .WithOpenApi();

        // callback is the client page Last.fm should send the user back to;
        // Last.fm appends ?token=... which the client then POSTs to /session.
        lastFmGroup.MapGet("/auth-url", (string callback, LastFmClient lastFm) =>
        {
            if (!lastFm.IsConfigured)
            {
                return Results.Problem("Last.fm is not configured on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!Uri.TryCreate(callback, UriKind.Absolute, out var callbackUri) ||
                callbackUri.Scheme is not ("http" or "https"))
            {
                return Results.BadRequest("callback must be an absolute http(s) URL.");
            }

            return Results.Ok(new LastFmAuthUrlDto(lastFm.GetAuthorizeUrl(callbackUri.ToString())));
        })
        .WithName("GetLastFmAuthUrl")
        .WithOpenApi();

        lastFmGroup.MapPost("/session", async (
            LastFmLinkRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            LastFmClient lastFm,
            LastFmAccountStore accounts,
            CancellationToken cancellationToken) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (!lastFm.IsConfigured)
            {
                return Results.Problem("Last.fm is not configured on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (string.IsNullOrWhiteSpace(request.Token))
            {
                return Results.BadRequest("token is required.");
            }

            var session = await lastFm.GetSessionAsync(request.Token, cancellationToken);
            if (!session.Success)
            {
                return Results.Problem($"Last.fm rejected the link: {session.ErrorMessage}", statusCode: StatusCodes.Status502BadGateway);
            }

            await accounts.SetAsync(user, session.SessionKey!, session.UserName);
            return Results.Ok(new LastFmStatusDto(true, true, session.UserName));
        })
        .WithName("LinkLastFm")
        .WithOpenApi();

        lastFmGroup.MapDelete("/session", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            LastFmClient lastFm,
            LastFmAccountStore accounts) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            await accounts.RemoveAsync(user);
            return Results.Ok(new LastFmStatusDto(lastFm.IsConfigured, false, null));
        })
        .WithName("UnlinkLastFm")
        .WithOpenApi();

        // Best-effort: returns 204 when there's nothing to do (Last.fm not
        // configured or not linked) so the player can call it unconditionally.
        lastFmGroup.MapPost("/now-playing", async (
            LastFmNowPlayingRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            FossegrimDbContext db,
            LastFmClient lastFm,
            LastFmAccountStore accounts,
            ILogger<LastFmAccountStore> logger,
            CancellationToken cancellationToken) =>
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (!lastFm.IsConfigured || await accounts.GetAsync(user) is not { } account)
            {
                return Results.NoContent();
            }

            var item = await db.MediaItems
                .AsNoTracking()
                .Include(m => m.Artists)
                .FirstOrDefaultAsync(m => m.Id == request.MediaItemId, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

            var artist = item.ArtistName ?? item.Artists.Select(a => a.Name).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(item.Title))
            {
                // Last.fm requires both; untagged files can't be reported.
                return Results.NoContent();
            }

            var track = new LastFmTrack(artist, item.Title, item.Album, TrackNumber: item.Track, Duration: item.Duration);
            var result = await lastFm.UpdateNowPlayingAsync(account.SessionKey, track, cancellationToken);
            if (result.Success)
            {
                return Results.NoContent();
            }

            if (result.ErrorCode == InvalidSessionKeyError)
            {
                logger.LogInformation("Last.fm session for user {UserId} was revoked; unlinking", user.Id);
                await accounts.RemoveAsync(user);
            }
            else
            {
                logger.LogWarning("Last.fm now-playing failed for user {UserId}: {Error} {Message}", user.Id, result.ErrorCode, result.ErrorMessage);
            }

            return Results.Problem($"Last.fm now-playing failed: {result.ErrorMessage}", statusCode: StatusCodes.Status502BadGateway);
        })
        .WithName("UpdateLastFmNowPlaying")
        .WithOpenApi();
    }
}
