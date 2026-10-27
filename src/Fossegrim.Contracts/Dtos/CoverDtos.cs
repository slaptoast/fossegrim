namespace Fossegrim.Contracts.Dtos;

// One cover a search found. PreviewUrl is a small image for the results;
// ImageUrl is the larger one (about 1000-1200px) that's saved.
public record CoverSearchResultDto(
    string Provider,
    string Title,
    string? Artist,
    string? Year,
    string PreviewUrl,
    string ImageUrl);

// Where a cover search provider stands. State is "ready", "limited" (wait
// RetryAfterSeconds: either the provider refused, or this server's share of
// its limit is used up) or "unavailable" (it couldn't be reached or answered
// with an error). Message explains it for people. Remaining/Limit/Window
// describe the provider's request limit, which every user of the server shares.
public record CoverProviderStatusDto(
    string Provider,
    string Name,
    string State,
    string Message,
    int? RetryAfterSeconds,
    int RemainingRequests,
    int RequestLimit,
    int WindowSeconds);

public record CoverSearchResponse(
    IReadOnlyList<CoverSearchResultDto> Results,
    IReadOnlyList<CoverProviderStatusDto> Providers);

// Saves a search result: the server downloads ImageUrl itself, from the
// providers' image hosts only.
public record SetCoverFromSearchRequest(string ImageUrl);

// Updated counts the tracks whose files now carry the cover.
public record SetAlbumCoverResponse(int Updated, IReadOnlyList<MediaItemTagUpdateError> Errors);
