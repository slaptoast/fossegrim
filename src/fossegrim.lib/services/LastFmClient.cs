using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Fossegrim.Lib.Services;

/// <summary>
/// Track details sent to Last.fm. Artist and Track are required by the API;
/// everything else is optional and omitted when null.
/// </summary>
public record LastFmTrack(
    string Artist,
    string Track,
    string? Album = null,
    string? AlbumArtist = null,
    int? TrackNumber = null,
    TimeSpan? Duration = null);

public record LastFmResult(bool Success, int? ErrorCode = null, string? ErrorMessage = null);

/// <summary>
/// Minimal client for the documented Last.fm scrobbling calls
/// (track.updateNowPlaying and track.scrobble). Every call needs a user
/// session key (sk) obtained through Last.fm's auth flow; obtaining and
/// storing that key is left to the caller. Does nothing unless
/// <see cref="LastFmOptions"/> is configured.
/// See https://www.last.fm/api/scrobbling and https://www.last.fm/api/authspec.
/// </summary>
public class LastFmClient
{
    private readonly HttpClient _http;
    private readonly LastFmOptions _options;

    public LastFmClient(HttpClient http, IOptions<LastFmOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public bool IsConfigured => _options.IsConfigured;

    public Task<LastFmResult> UpdateNowPlayingAsync(string sessionKey, LastFmTrack track, CancellationToken cancellationToken = default)
    {
        var parameters = TrackParameters(track);
        return PostSignedAsync("track.updateNowPlaying", sessionKey, parameters, cancellationToken);
    }

    // startedAt is when the track started playing; Last.fm expects it as a
    // Unix timestamp.
    public Task<LastFmResult> ScrobbleAsync(string sessionKey, LastFmTrack track, DateTimeOffset startedAt, CancellationToken cancellationToken = default)
    {
        var parameters = TrackParameters(track);
        parameters["timestamp"] = startedAt.ToUnixTimeSeconds().ToString();
        return PostSignedAsync("track.scrobble", sessionKey, parameters, cancellationToken);
    }

    /// <summary>
    /// Last.fm's documented scrobble rule: the track must be longer than 30
    /// seconds and have been played for at least half its duration or for
    /// 4 minutes, whichever comes first.
    /// </summary>
    public static bool IsScrobbleEligible(TimeSpan trackDuration, TimeSpan playedFor)
    {
        if (trackDuration <= TimeSpan.FromSeconds(30))
        {
            return false;
        }

        var threshold = TimeSpan.FromTicks(Math.Min(trackDuration.Ticks / 2, TimeSpan.FromMinutes(4).Ticks));
        return playedFor >= threshold;
    }

    private static Dictionary<string, string> TrackParameters(LastFmTrack track)
    {
        var parameters = new Dictionary<string, string>
        {
            ["artist"] = track.Artist,
            ["track"] = track.Track,
        };

        if (!string.IsNullOrWhiteSpace(track.Album))
        {
            parameters["album"] = track.Album;
        }
        if (!string.IsNullOrWhiteSpace(track.AlbumArtist))
        {
            parameters["albumArtist"] = track.AlbumArtist;
        }
        if (track.TrackNumber is > 0)
        {
            parameters["trackNumber"] = track.TrackNumber.Value.ToString();
        }
        if (track.Duration is { } duration && duration > TimeSpan.Zero)
        {
            parameters["duration"] = ((int)duration.TotalSeconds).ToString();
        }

        return parameters;
    }

    private async Task<LastFmResult> PostSignedAsync(
        string method,
        string sessionKey,
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new LastFmResult(false, ErrorMessage: "Last.fm is not configured.");
        }

        parameters["method"] = method;
        parameters["api_key"] = _options.ApiKey!;
        parameters["sk"] = sessionKey;
        parameters["api_sig"] = Sign(parameters, _options.ApiSecret!);
        // format is excluded from the signature, so it's added afterwards.
        parameters["format"] = "json";

        try
        {
            using var response = await _http.PostAsync(_options.ApiUrl, new FormUrlEncodedContent(parameters), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // Errors come back as {"error": <code>, "message": "..."}, with
            // either a 2xx or non-2xx status.
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("error", out var error))
            {
                var message = json.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
                return new LastFmResult(false, error.TryGetInt32(out var code) ? code : null, message);
            }

            return response.IsSuccessStatusCode
                ? new LastFmResult(true)
                : new LastFmResult(false, ErrorMessage: $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new LastFmResult(false, ErrorMessage: ex.Message);
        }
    }

    // api_sig: every parameter (except format/callback) sorted by name,
    // concatenated as <name><value>, followed by the shared secret, then
    // MD5-hashed as UTF-8 and hex-encoded.
    public static string Sign(IReadOnlyDictionary<string, string> parameters, string secret)
    {
        var builder = new StringBuilder();
        foreach (var (name, value) in parameters
            .Where(p => p.Key is not ("format" or "callback" or "api_sig"))
            .OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(name).Append(value);
        }
        builder.Append(secret);

        var hash = MD5.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
