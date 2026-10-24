namespace Fossegrim.Contracts.Dtos;

// StartedAt is when the track began playing; ListenedFor is time actually
// heard (excluding seeks). Both optional; StartedAt defaults to now.
public record RecordPlayRequest(Guid MediaItemId, DateTimeOffset? StartedAt = null, TimeSpan? ListenedFor = null);

public record PlayEventDto(
    Guid Id,
    DateTime PlayedAt,
    TimeSpan? ListenedFor,
    PlaylistTrackDto MediaItem);
