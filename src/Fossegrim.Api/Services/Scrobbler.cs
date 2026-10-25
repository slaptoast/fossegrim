using Fossegrim.Lib.Data;
using Fossegrim.Lib.Enums;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Services;

/// <summary>
/// Sends plays to Last.fm. A play is marked Pending when it's recorded (if
/// the user has linked Last.fm and the play meets the scrobble rule);
/// <see cref="ScrobbleWorker"/> then calls <see cref="ProcessDueAsync"/> to
/// send it, retrying temporary failures with backoff and giving up on
/// permanent ones or once the play is too old for Last.fm to accept.
/// </summary>
public class Scrobbler
{
    public const int BatchSize = 50;

    // Last.fm doesn't accept scrobbles more than 14 days old.
    public static readonly TimeSpan MaxScrobbleAge = TimeSpan.FromDays(14);

    // Delay before retry n (the last one repeats until the play is too old).
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12),
    ];

    // Last.fm errors worth retrying: 8 operation failed, 11 service offline,
    // 16 temporarily unavailable, 29 rate limit. 10/13/26 (bad or suspended
    // API key, bad signature) are server configuration problems an admin can
    // fix, so those plays wait rather than being dropped.
    private static readonly HashSet<int> RetryableErrors = [8, 10, 11, 13, 16, 26, 29];
    private const int InvalidSessionKeyError = 9;
    private const int DailyLimitIgnoredCode = 5;

    private readonly FossegrimDbContext _db;
    private readonly LastFmClient _lastFm;
    private readonly LastFmAccountStore _accounts;
    private readonly ILogger<Scrobbler> _logger;

    public Scrobbler(FossegrimDbContext db, LastFmClient lastFm, LastFmAccountStore accounts, ILogger<Scrobbler> logger)
    {
        _db = db;
        _lastFm = lastFm;
        _accounts = accounts;
        _logger = logger;
    }

    /// <summary>
    /// Marks a just-recorded play Pending if it should go to Last.fm. Doesn't
    /// save; the caller saves the play along with it.
    /// </summary>
    public async Task MarkForScrobblingAsync(PlayEvent play, MediaItem track, ApplicationUser user)
    {
        if (string.IsNullOrWhiteSpace(track.ArtistName))
        {
            // ToLastFmTrack falls back to the linked artists.
            await _db.Entry(track).Collection(t => t.Artists).LoadAsync();
        }

        if (!_lastFm.IsConfigured || ToLastFmTrack(track) is null || !MeetsScrobbleRule(play, track))
        {
            return;
        }

        if (await _accounts.GetAsync(user) is null)
        {
            return;
        }

        play.ScrobbleStatus = ScrobbleStatus.Pending;
        play.NextScrobbleAttemptAt = play.RecordedAt;
    }

    /// <summary>
    /// Sends up to <see cref="BatchSize"/> due Pending plays; returns how
    /// many were processed (a full batch means more may be waiting).
    /// </summary>
    public async Task<int> ProcessDueAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        if (!_lastFm.IsConfigured)
        {
            return 0;
        }

        var due = await _db.PlayEvents
            .Include(pe => pe.MediaItem!).ThenInclude(m => m.Artists)
            .Include(pe => pe.User)
            .Where(pe => pe.ScrobbleStatus == ScrobbleStatus.Pending && pe.NextScrobbleAttemptAt <= utcNow)
            .OrderBy(pe => pe.NextScrobbleAttemptAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var accounts = new Dictionary<string, LastFmAccount?>();
        foreach (var play in due)
        {
            if (!accounts.TryGetValue(play.UserId, out var account))
            {
                account = await _accounts.GetAsync(play.User!);
                accounts[play.UserId] = account;
            }

            var revoked = await ProcessAsync(play, account, utcNow, cancellationToken);
            if (revoked)
            {
                // Don't try the user's remaining plays with the revoked key.
                await _accounts.RemoveAsync(play.User!);
                accounts[play.UserId] = null;
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        return due.Count;
    }

    /// <summary>When the next Pending play is due, or null if none are.</summary>
    public Task<DateTime?> NextDueAsync(CancellationToken cancellationToken = default) =>
        _db.PlayEvents
            .Where(pe => pe.ScrobbleStatus == ScrobbleStatus.Pending)
            .MinAsync(pe => pe.NextScrobbleAttemptAt, cancellationToken);

    // Returns true when Last.fm says the user's session key was revoked.
    private async Task<bool> ProcessAsync(PlayEvent play, LastFmAccount? account, DateTime utcNow, CancellationToken cancellationToken)
    {
        if (utcNow - play.PlayedAt > MaxScrobbleAge)
        {
            GiveUp(play, "Too old for Last.fm, which only accepts plays from the last 14 days.");
            return false;
        }

        if (account is null)
        {
            GiveUp(play, "Last.fm isn't linked.");
            return false;
        }

        if (play.MediaItem is null || ToLastFmTrack(play.MediaItem) is not { } track)
        {
            GiveUp(play, "The track needs an artist and title for Last.fm.");
            return false;
        }

        var startedAt = new DateTimeOffset(DateTime.SpecifyKind(play.PlayedAt, DateTimeKind.Utc));
        var result = await _lastFm.ScrobbleAsync(account.SessionKey, track, startedAt, cancellationToken);
        play.ScrobbleAttempts++;

        if (result.Success && result.IgnoredCode is null)
        {
            play.ScrobbleStatus = ScrobbleStatus.Scrobbled;
            play.ScrobbledAt = utcNow;
            play.NextScrobbleAttemptAt = null;
            play.ScrobbleError = null;
        }
        else if (result.Success && result.IgnoredCode == DailyLimitIgnoredCode)
        {
            RetryLater(play, utcNow, result.ErrorMessage);
        }
        else if (result.Success)
        {
            play.ScrobbleStatus = ScrobbleStatus.Ignored;
            play.ScrobbledAt = utcNow;
            play.NextScrobbleAttemptAt = null;
            play.ScrobbleError = Truncate(result.ErrorMessage);
        }
        else if (result.ErrorCode == InvalidSessionKeyError)
        {
            GiveUp(play, "Last.fm access was revoked; link Last.fm again from your Profile.");
        }
        else if (result.ErrorCode is null || RetryableErrors.Contains(result.ErrorCode.Value))
        {
            // No code means a network error, a timeout or an HTTP error page.
            RetryLater(play, utcNow, result.ErrorMessage);
        }
        else
        {
            GiveUp(play, $"Last.fm error {result.ErrorCode}: {result.ErrorMessage}");
        }

        if (play.ScrobbleStatus != ScrobbleStatus.Scrobbled)
        {
            _logger.LogInformation("Scrobble of play {PlayId} for user {UserId}: {Status} ({Error})",
                play.Id, play.UserId, play.ScrobbleStatus, play.ScrobbleError);
        }

        return result.ErrorCode == InvalidSessionKeyError;
    }

    private static void RetryLater(PlayEvent play, DateTime utcNow, string? error)
    {
        var delay = RetryDelays[Math.Clamp(play.ScrobbleAttempts - 1, 0, RetryDelays.Length - 1)];
        play.ScrobbleStatus = ScrobbleStatus.Pending;
        play.NextScrobbleAttemptAt = utcNow + delay;
        play.ScrobbleError = Truncate(error);
    }

    private static void GiveUp(PlayEvent play, string error)
    {
        play.ScrobbleStatus = ScrobbleStatus.Failed;
        play.NextScrobbleAttemptAt = null;
        play.ScrobbleError = Truncate(error);
    }

    // The browser only reports a play once ListenRule is met; this re-checks
    // it so other clients can't scrobble skipped tracks. Without a known track
    // length, it settles for the 30-second minimum.
    private static bool MeetsScrobbleRule(PlayEvent play, MediaItem track)
    {
        if (play.ListenedFor is not { } listened)
        {
            return false;
        }

        return track.Duration is { } duration && duration > TimeSpan.Zero
            ? ListenRule.IsListen(duration, listened)
            : listened > ListenRule.MinTrackLength;
    }

    public static LastFmTrack? ToLastFmTrack(MediaItem item)
    {
        var artist = item.ArtistName;
        if (string.IsNullOrWhiteSpace(artist))
        {
            artist = item.Artists.Select(a => a.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        }

        return string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(item.Title)
            ? null
            : new LastFmTrack(artist, item.Title, item.Album, TrackNumber: item.Track, Duration: item.Duration);
    }

    private static string? Truncate(string? message) =>
        message is { Length: > 500 } ? message[..500] : message;
}
