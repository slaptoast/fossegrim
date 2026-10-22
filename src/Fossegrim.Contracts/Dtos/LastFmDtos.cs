namespace Fossegrim.Contracts.Dtos;

// Configured is false when the server has no Last.fm API key/secret, in
// which case clients should hide the Connect option entirely.
public record LastFmStatusDto(bool Configured, bool Connected, string? UserName);

public record LastFmAuthUrlDto(string Url);

public record LastFmLinkRequest(string Token);

public record LastFmNowPlayingRequest(Guid MediaItemId);

// Admin view of the server's Last.fm API credentials. The secret is never
// returned; SetByEnvironment means LASTFM_API_KEY/SECRET are set and the
// saved values (if any) are ignored.
public record LastFmSettingsDto(bool Configured, bool SetByEnvironment, string? ApiKey, bool HasSecret);

// A blank ApiSecret keeps the saved one, so the key can be changed without
// re-entering the secret.
public record UpdateLastFmSettingsRequest(string ApiKey, string? ApiSecret);
