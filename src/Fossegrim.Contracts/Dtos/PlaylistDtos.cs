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
    IEnumerable<PlaylistItemDto> Items,
    IEnumerable<PlaylistShareDto> Shares);

public record PlaylistItemDto(
    Guid Id,
    int Position,
    DateTime? DateAdded,
    MediaItemSummaryDto MediaItem);

public record PlaylistShareDto(
    string UserId,
    string? DisplayName,
    string ShareType);

public record CreatePlaylistRequest(string Name);

public record UpdatePlaylistRequest(string Name);

public record AddPlaylistItemsRequest(IEnumerable<Guid> MediaItemIds);

public record ReorderPlaylistItemsRequest(IEnumerable<Guid> ItemIds);

public record SharePlaylistRequest(string UserId, string ShareType);
