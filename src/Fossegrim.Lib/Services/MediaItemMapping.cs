using System.Linq.Expressions;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services.Library;

namespace Fossegrim.Lib.Services;

/// <summary>
/// The one MediaItem → MediaItemDto mapping, usable in EF queries
/// (<c>.Select(MediaItemMapping.ToDto)</c>) and in memory (<see cref="Map"/>).
/// </summary>
public static class MediaItemMapping
{
    public static readonly Expression<Func<MediaItem, MediaItemDto>> ToDto = m => new MediaItemDto(
        m.Id,
        m.Title,
        m.ArtistName,
        m.Album,
        m.DateAdded,
        m.LastModified,
        m.Year,
        m.Track,
        m.Genre,
        m.Duration,
        m.Bitrate,
        m.Artists.Select(a => new ArtistSummaryDto(a.Id, a.Name)),
        m.Albums.Select(al => new AlbumSummaryDto(al.Id, al.Name, al.Year)),
        m.Disc,
        m.FileSize,
        // Runs client-side: it's the last step of the query.
        m.FileLocation == null ? null : AudioFormats.ContentType(m.FileLocation));

    private static readonly Func<MediaItem, MediaItemDto> Compiled = ToDto.Compile();

    public static MediaItemDto Map(MediaItem item) => Compiled(item);
}
