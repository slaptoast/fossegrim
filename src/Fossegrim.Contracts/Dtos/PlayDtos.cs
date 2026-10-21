namespace Fossegrim.Contracts.Dtos;

public record RecordPlayRequest(Guid MediaItemId);

public record PlayEventDto(
    Guid Id,
    DateTime PlayedAt,
    PlaylistTrackDto MediaItem);
