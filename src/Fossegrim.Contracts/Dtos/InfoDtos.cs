namespace Fossegrim.Contracts.Dtos;

// What the hover cards show. Summary and InfoUrl are a Wikipedia summary and
// its page, cached on the server. NeedsLookup: the cache is empty or old,
// so ask again with ?lookup=true to fetch it (slower: a few seconds).
// Plays and LastPlayedAt are the caller's own.

public record AlbumInfoDto(
    Guid Id,
    string Name,
    IReadOnlyList<string> Artists,
    int? Year,
    string? Genre,
    string? Label,
    int TrackCount,
    TimeSpan? Length,
    int Plays,
    DateTime? LastPlayedAt,
    string? Summary,
    string? InfoUrl,
    bool NeedsLookup);

public record ArtistInfoDto(
    Guid Id,
    string Name,
    string? Country,
    int AlbumCount,
    int TrackCount,
    int Plays,
    DateTime? LastPlayedAt,
    string? Summary,
    string? InfoUrl,
    bool NeedsLookup);
