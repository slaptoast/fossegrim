namespace Fossegrim.Contracts.Dtos;

// Configured is false when the server has no Last.fm API key/secret, in
// which case clients should hide the Connect option entirely.
public record LastFmStatusDto(bool Configured, bool Connected, string? UserName);

public record LastFmAuthUrlDto(string Url);

public record LastFmLinkRequest(string Token);

public record LastFmNowPlayingRequest(Guid MediaItemId);
