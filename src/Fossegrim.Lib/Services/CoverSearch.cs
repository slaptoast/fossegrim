using System.Net;
using System.Text;
using System.Text.Json;
using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Lib.Services;

/// <summary>
/// One provider's request limit, shared by every search on this server (the
/// providers count requests per server, not per user). Requests beyond the
/// limit aren't sent. After the provider itself refuses one, nothing is sent
/// until it said (or we assume) it's ready again. Thread-safe.
/// </summary>
public sealed class ProviderRateLimit
{
    private readonly object _lock = new();
    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly TimeProvider _time;
    private DateTimeOffset _blockedUntil;
    private string? _blockedReason;
    private string? _lastError;

    public ProviderRateLimit(int limit, TimeSpan window, TimeProvider time)
    {
        Limit = limit;
        Window = window;
        _time = time;
    }

    public int Limit { get; }
    public TimeSpan Window { get; }

    /// <summary>
    /// Takes a request slot, waiting up to <paramref name="maxWait"/> for one.
    /// Returns null when granted, otherwise how long until one frees up.
    /// </summary>
    public async Task<TimeSpan?> AcquireAsync(TimeSpan maxWait, CancellationToken cancellationToken)
    {
        var wait = TryTake();
        if (wait is null || wait > maxWait)
        {
            return wait;
        }

        await Task.Delay(wait.Value, _time, cancellationToken);
        return TryTake();
    }

    /// <summary>The provider refused a request: send nothing for <paramref name="duration"/>.</summary>
    public void Block(TimeSpan duration, string reason)
    {
        lock (_lock)
        {
            _blockedUntil = _time.GetUtcNow() + duration;
            _blockedReason = reason;
            _lastError = null;
        }
    }

    public void MarkFailed(string error)
    {
        lock (_lock)
        {
            _lastError = error;
        }
    }

    public void MarkSucceeded()
    {
        lock (_lock)
        {
            _lastError = null;
            _blockedReason = null;
        }
    }

    public RateLimitSnapshot Snapshot()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            var blocked = _blockedUntil > now ? _blockedUntil - now : (TimeSpan?)null;
            var full = _sent.Count >= Limit ? _sent.Peek() + Window - now : (TimeSpan?)null;
            return new RateLimitSnapshot(
                Remaining: Math.Max(0, Limit - _sent.Count),
                BlockedFor: blocked,
                BlockedReason: blocked is null ? null : _blockedReason,
                FullFor: full,
                LastError: _lastError);
        }
    }

    private TimeSpan? TryTake()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            if (_blockedUntil > now)
            {
                return _blockedUntil - now;
            }
            if (_sent.Count >= Limit)
            {
                return _sent.Peek() + Window - now;
            }

            _sent.Enqueue(now);
            return null;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        while (_sent.Count > 0 && _sent.Peek() + Window <= now)
        {
            _sent.Dequeue();
        }
    }
}

public record RateLimitSnapshot(int Remaining, TimeSpan? BlockedFor, string? BlockedReason, TimeSpan? FullFor, string? LastError);

/// <summary>The request limits of the cover search providers. A singleton: limits are per server.</summary>
public sealed class CoverSearchLimits
{
    public CoverSearchLimits(TimeProvider time)
    {
        // https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting: one request per second on average.
        MusicBrainz = new ProviderRateLimit(1, TimeSpan.FromSeconds(1), time);
        // Deezer's documented quota: 50 requests per 5 seconds.
        Deezer = new ProviderRateLimit(50, TimeSpan.FromSeconds(5), time);
    }

    public ProviderRateLimit MusicBrainz { get; }
    public ProviderRateLimit Deezer { get; }
}

/// <summary>
/// Searches the free cover art services: MusicBrainz (for the release) with
/// the Cover Art Archive (for its art), and Deezer. Neither needs a key.
/// Each provider's status says whether it can search now and, if not, why
/// and for how long.
/// </summary>
public class CoverSearchService
{
    public const string MusicBrainzId = "musicbrainz";
    public const string DeezerId = "deezer";

    private const int MaxResultsPerProvider = 8;
    // A search that needs a MusicBrainz slot waits this long for one rather than fail.
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultMusicBrainzBlock = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultDeezerBlock = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly CoverSearchLimits _limits;

    public CoverSearchService(HttpClient http, CoverSearchLimits limits)
    {
        _http = http;
        _limits = limits;
    }

    // MusicBrainz asks every client to identify itself and how to reach its authors.
    public static string UserAgent =>
        $"Fossegrim/{Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev"} ( https://github.com/slaptoast/fossegrim )";

    public async Task<CoverSearchResponse> SearchAsync(string album, string? artist, CancellationToken cancellationToken = default)
    {
        var musicBrainz = SearchMusicBrainzAsync(album.Trim(), artist?.Trim(), cancellationToken);
        var deezer = SearchDeezerAsync(album.Trim(), artist?.Trim(), cancellationToken);
        var results = (await musicBrainz).Concat(await deezer).ToList();
        return new CoverSearchResponse(results, GetStatuses());
    }

    public IReadOnlyList<CoverProviderStatusDto> GetStatuses() =>
    [
        Status(MusicBrainzId, "MusicBrainz", _limits.MusicBrainz, "1 search per second"),
        Status(DeezerId, "Deezer", _limits.Deezer, "50 searches per 5 seconds"),
    ];

    private static CoverProviderStatusDto Status(string id, string name, ProviderRateLimit limit, string limitText)
    {
        var snapshot = limit.Snapshot();
        var (state, message, retry) = snapshot switch
        {
            { BlockedFor: { } blocked } => ("limited", snapshot.BlockedReason ?? $"{name} refused the last search.", blocked),
            // A search waits up to MaxWait for a slot, so a shorter wait isn't worth reporting.
            { FullFor: { } full } when full > MaxWait => ("limited", $"Searching too quickly: {name} allows {limitText} for this whole server.", full),
            { LastError: { } error } => ("unavailable", error, (TimeSpan?)null),
            _ => ("ready", $"Ready. {name} allows {limitText}.", (TimeSpan?)null),
        };
        return new CoverProviderStatusDto(
            id, name, state, message,
            retry is null ? null : (int)Math.Ceiling(retry.Value.TotalSeconds),
            snapshot.Remaining, limit.Limit, (int)limit.Window.TotalSeconds);
    }

    // ---- MusicBrainz + Cover Art Archive ----

    private async Task<IReadOnlyList<CoverSearchResultDto>> SearchMusicBrainzAsync(string album, string? artist, CancellationToken cancellationToken)
    {
        var limit = _limits.MusicBrainz;
        if (await limit.AcquireAsync(MaxWait, cancellationToken) is not null)
        {
            return []; // the status explains when it can search again
        }

        var query = $"releasegroup:\"{EscapeLucene(album)}\"";
        if (!string.IsNullOrEmpty(artist))
        {
            query += $" AND artist:\"{EscapeLucene(artist)}\"";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://musicbrainz.org/ws/2/release-group?query={Uri.EscapeDataString(query)}&fmt=json&limit={MaxResultsPerProvider}");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/json");

        JsonDocument json;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
            {
                limit.Block(RetryAfter(response) ?? DefaultMusicBrainzBlock,
                    $"MusicBrainz is limiting requests and refused the search (HTTP {(int)response.StatusCode}). It allows about one search per second for this whole server.");
                return [];
            }
            if (!response.IsSuccessStatusCode)
            {
                limit.MarkFailed($"MusicBrainz answered with an error (HTTP {(int)response.StatusCode}).");
                return [];
            }
            json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            limit.MarkFailed($"Couldn't reach MusicBrainz: {Describe(ex)}");
            return [];
        }

        using (json)
        {
            limit.MarkSucceeded();
            if (!json.RootElement.TryGetProperty("release-groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var found = groups.EnumerateArray()
                .Select(g => (
                    Id: g.TryGetProperty("id", out var id) ? id.GetString() : null,
                    Title: g.TryGetProperty("title", out var title) ? title.GetString() : null,
                    Artist: ArtistCredit(g),
                    Year: g.TryGetProperty("first-release-date", out var date) && date.GetString() is { Length: >= 4 } d ? d[..4] : null))
                .Where(g => g.Id is not null && g.Title is not null)
                .ToList();

            // The Cover Art Archive isn't rate limited like MusicBrainz; groups without art are skipped.
            var covers = await Task.WhenAll(found.Select(g => FindReleaseGroupCoverAsync(g.Id!, cancellationToken)));
            return found.Zip(covers)
                .Where(pair => pair.Second is not null)
                .Select(pair => new CoverSearchResultDto(MusicBrainzId, pair.First.Title!, pair.First.Artist, pair.First.Year, pair.Second!.Value.Preview, pair.Second.Value.Image))
                .ToList();
        }
    }

    private async Task<(string Preview, string Image)?> FindReleaseGroupCoverAsync(string releaseGroupId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://coverartarchive.org/release-group/{Uri.EscapeDataString(releaseGroupId)}");
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null; // 404: no art for this release group
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!json.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var image = images.EnumerateArray()
                .OrderByDescending(i => i.TryGetProperty("front", out var front) && front.ValueKind == JsonValueKind.True)
                .Cast<JsonElement?>()
                .FirstOrDefault();
            if (image is not { } chosen)
            {
                return null;
            }

            var thumbnails = chosen.TryGetProperty("thumbnails", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
            string? Thumbnail(params string[] sizes) => sizes
                .Select(size => thumbnails.ValueKind == JsonValueKind.Object && thumbnails.TryGetProperty(size, out var url) ? url.GetString() : null)
                .FirstOrDefault(url => !string.IsNullOrEmpty(url));

            var full = Thumbnail("1200") ?? (chosen.TryGetProperty("image", out var original) ? original.GetString() : null);
            var preview = Thumbnail("250", "small", "500", "large") ?? full;
            return full is null || preview is null ? null : (Https(preview), Https(full));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private static string? ArtistCredit(JsonElement group)
    {
        if (!group.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var credit in credits.EnumerateArray())
        {
            text.Append(credit.TryGetProperty("name", out var name) ? name.GetString() : null);
            text.Append(credit.TryGetProperty("joinphrase", out var join) ? join.GetString() : null);
        }
        return text.Length > 0 ? text.ToString() : null;
    }

    // ---- Deezer ----

    private async Task<IReadOnlyList<CoverSearchResultDto>> SearchDeezerAsync(string album, string? artist, CancellationToken cancellationToken)
    {
        var limit = _limits.Deezer;
        if (await limit.AcquireAsync(MaxWait, cancellationToken) is not null)
        {
            return [];
        }

        var query = $"album:\"{album.Replace("\"", "")}\"";
        if (!string.IsNullOrEmpty(artist))
        {
            query = $"artist:\"{artist.Replace("\"", "")}\" " + query;
        }

        JsonDocument json;
        try
        {
            using var response = await _http.GetAsync($"https://api.deezer.com/search/album?q={Uri.EscapeDataString(query)}&limit={MaxResultsPerProvider}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limit.Block(RetryAfter(response) ?? DefaultDeezerBlock,
                    "Deezer's request quota is used up (50 searches per 5 seconds for this whole server).");
                return [];
            }
            if (!response.IsSuccessStatusCode)
            {
                limit.MarkFailed($"Deezer answered with an error (HTTP {(int)response.StatusCode}).");
                return [];
            }
            json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            limit.MarkFailed($"Couldn't reach Deezer: {Describe(ex)}");
            return [];
        }

        using (json)
        {
            // Deezer reports errors, including its quota, as HTTP 200 with an error object.
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (code == 4)
                {
                    limit.Block(DefaultDeezerBlock, "Deezer's request quota is used up (50 searches per 5 seconds for this whole server).");
                }
                else
                {
                    limit.MarkFailed($"Deezer answered with an error: {message ?? "unknown error"}.");
                }
                return [];
            }

            limit.MarkSucceeded();
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return data.EnumerateArray()
                .Select(a => new
                {
                    Title = a.TryGetProperty("title", out var title) ? title.GetString() : null,
                    Artist = a.TryGetProperty("artist", out var ar) && ar.ValueKind == JsonValueKind.Object && ar.TryGetProperty("name", out var name) ? name.GetString() : null,
                    Preview = a.TryGetProperty("cover_medium", out var medium) ? medium.GetString() : null,
                    Image = a.TryGetProperty("cover_xl", out var xl) ? xl.GetString() : null,
                })
                .Where(a => !string.IsNullOrEmpty(a.Title) && !string.IsNullOrEmpty(a.Image))
                .Select(a => new CoverSearchResultDto(DeezerId, a.Title!, a.Artist, null, Https(a.Preview ?? a.Image!), Https(a.Image!)))
                .ToList();
        }
    }

    // ---- Helpers ----

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta)
        {
            return delta;
        }
        if (retry?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
        }
        return null;
    }

    private static string EscapeLucene(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Https(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url["http://".Length..] : url;

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "it didn't answer in time.",
        JsonException => "it sent an answer that couldn't be read.",
        _ => ex.Message,
    };
}
