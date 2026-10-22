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

public record LastFmSessionResult(bool Success, string? SessionKey = null, string? UserName = null, int? ErrorCode = null, string? ErrorMessage = null);

/// <summary>
/// Minimal client for Last.fm's web auth flow (auth.getSession) and the
/// documented scrobbling calls (track.updateNowPlaying and track.scrobble).
/// Scrobbling calls need the user session key (sk) that auth.getSession
/// returns; storing that key is left to the caller. Does nothing unless
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

    /// <summary>
    /// The Last.fm page that asks the user to grant access. Last.fm then
    /// redirects to <paramref name="callbackUrl"/> with a ?token= to pass to
    /// <see cref="GetSessionAsync"/>.
    /// </summary>
    public string GetAuthorizeUrl(string callbackUrl) =>
        $"{_options.AuthUrl}?api_key={Uri.EscapeDataString(_options.ApiKey ?? "")}&cb={Uri.EscapeDataString(callbackUrl)}";

    // Exchanges the token from the auth callback for a session key, which
    // doesn't expire until the user revokes access on Last.fm.
    public async Task<LastFmSessionResult> GetSessionAsync(string token, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string> { ["token"] = token };
        var (result, root) = await PostSignedAsync("auth.getSession", sessionKey: null, parameters, cancellationToken);
        if (!result.Success)
        {
            return new LastFmSessionResult(false, ErrorCode: result.ErrorCode, ErrorMessage: result.ErrorMessage);
        }

        if (root is { } json &&
            json.TryGetProperty("session", out var session) &&
            session.TryGetProperty("key", out var key) &&
            key.GetString() is { Length: > 0 } sessionKey)
        {
            var userName = session.TryGetProperty("name", out var name) ? name.GetString() : null;
            return new LastFmSessionResult(true, sessionKey, userName);
        }

        return new LastFmSessionResult(false, ErrorMessage: "Last.fm returned no session key.");
    }

    public Task<LastFmResult> UpdateNowPlayingAsync(string sessionKey, LastFmTrack track, CancellationToken cancellationToken = default)
    {
        var parameters = TrackParameters(track);
        return PostTrackAsync("track.updateNowPlaying", sessionKey, parameters, cancellationToken);
    }

    // startedAt is when the track started playing; Last.fm expects it as a
    // Unix timestamp.
    public Task<LastFmResult> ScrobbleAsync(string sessionKey, LastFmTrack track, DateTimeOffset startedAt, CancellationToken cancellationToken = default)
    {
        var parameters = TrackParameters(track);
        parameters["timestamp"] = startedAt.ToUnixTimeSeconds().ToString();
        return PostTrackAsync("track.scrobble", sessionKey, parameters, cancellationToken);
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

    private async Task<LastFmResult> PostTrackAsync(
        string method,
        string sessionKey,
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var (result, _) = await PostSignedAsync(method, sessionKey, parameters, cancellationToken);
        return result;
    }

    // Returns the parsed response body alongside the result so callers that
    // need data back (auth.getSession) can read it.
    private async Task<(LastFmResult Result, JsonElement? Root)> PostSignedAsync(
        string method,
        string? sessionKey,
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return (new LastFmResult(false, ErrorMessage: "Last.fm is not configured."), null);
        }

        parameters["method"] = method;
        parameters["api_key"] = _options.ApiKey!;
        if (sessionKey is not null)
        {
            parameters["sk"] = sessionKey;
        }
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
            var root = json.RootElement.Clone();
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var error))
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                return (new LastFmResult(false, error.TryGetInt32(out var code) ? code : null, message), root);
            }

            return response.IsSuccessStatusCode
                ? (new LastFmResult(true), root)
                : (new LastFmResult(false, ErrorMessage: $"HTTP {(int)response.StatusCode}"), root);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return (new LastFmResult(false, ErrorMessage: ex.Message), null);
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
