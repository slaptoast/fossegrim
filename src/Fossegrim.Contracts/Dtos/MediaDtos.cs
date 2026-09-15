namespace Fossegrim.Contracts.Dtos;

public record ArtistDto(
    Guid Id,
    string Name,
    string? Bio,
    string? Country,
    string? ImageUrl,
    DateTime? DateAdded,
    DateTime? LastModified,
    IEnumerable<MediaItemSummaryDto> MediaItems,
    IEnumerable<AlbumSummaryDto> Albums);

public record AlbumDto(
    Guid Id,
    string Name,
    int? Year,
    string? Genre,
    string? Label,
    string? CoverImageUrl,
    DateTime? DateAdded,
    DateTime? LastModified,
    IEnumerable<MediaItemSummaryDto> MediaItems,
    IEnumerable<ArtistSummaryDto> Artists);

public record MediaItemDto(
    Guid Id,
    string? Title,
    string? ArtistName,
    string? Album,
    DateTime? DateAdded,
    DateTime? LastModified,
    int? Year,
    int? Track,
    string? Genre,
    TimeSpan? Duration,
    int? Bitrate,
    IEnumerable<ArtistSummaryDto> Artists,
    IEnumerable<AlbumSummaryDto> Albums);

public record ArtistSummaryDto(Guid Id, string Name);

public record AlbumSummaryDto(Guid Id, string Name, int? Year);

public record MediaItemSummaryDto(Guid Id, string? Title, TimeSpan? Duration);
