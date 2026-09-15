namespace Fossegrim.Contracts.Dtos;

public record PlaylistDto(
    Guid Id,
    string Name,
    string Type,
    string OwnerId,
    string? OwnerDisplayName,
    string AccessLevel,
    DateTime? DateAdded,
    DateTime? LastModified,
    int TrackCount);

public record PlaylistDetailDto(
    Guid Id,
    string Name,
    string Type,
    string OwnerId,
    string? OwnerDisplayName,
    string AccessLevel,
    DateTime? DateAdded,
    DateTime? LastModified,
    Guid? CurrentMediaItemId,
    IEnumerable<PlaylistItemDto> Items,
    IEnumerable<PlaylistShareDto> Shares);

public record PlaylistItemDto(
    Guid Id,
    double Position,
    DateTime? DateAdded,
    PlaylistTrackDto MediaItem);

public record PlaylistTrackDto(
    Guid Id,
    string? Title,
    string? ArtistName,
    string? Album,
    TimeSpan? Duration);

public record PlaylistShareDto(
    string UserId,
    string? DisplayName,
    string ShareType);

public record CreatePlaylistRequest(string Name);

public record UpdatePlaylistRequest(string Name);

public record AddPlaylistItemsRequest(IEnumerable<Guid> MediaItemIds);

// Moves ItemId to sit immediately after AfterItemId (or to the very start when AfterItemId is null).
public record MovePlaylistItemRequest(Guid? AfterItemId);

public record SharePlaylistRequest(string UserId, string ShareType);

// Replaces the caller's entire queue contents in one shot (e.g. "play this album now").
public record SetQueueRequest(IEnumerable<Guid> MediaItemIds, Guid? CurrentMediaItemId);

// Updates only the "now playing" pointer, without touching the queue's track list.
public record SetQueueCurrentTrackRequest(Guid? MediaItemId);
