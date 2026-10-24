namespace Fossegrim.Contracts.Dtos;

// StartedAt is when the track began playing; ListenedFor is time actually
// heard (excluding seeks). Both optional; StartedAt defaults to now.
public record RecordPlayRequest(Guid MediaItemId, DateTimeOffset? StartedAt = null, TimeSpan? ListenedFor = null);

// The track's tags as they are now (not when it was played), so a tag edit
// shows up in history too.
public record PlayedTrackDto(
    Guid Id,
    string? Title,
    string? ArtistName,
    string? Album,
    int? Year,
    int? Track,
    string? Genre,
    TimeSpan? Duration);

public record PlayEventDto(
    Guid Id,
    DateTime PlayedAt,
    TimeSpan? ListenedFor,
    PlayedTrackDto MediaItem);

public record PlayHistoryPageDto(
    IReadOnlyList<PlayEventDto> Items,
    int TotalCount,
    int Page,
    int PageSize);
