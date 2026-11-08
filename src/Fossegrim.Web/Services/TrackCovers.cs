using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Web.Services;

public static class TrackCovers
{
    /// <summary>The album whose cover stands for this track (its first), or null if it has none.</summary>
    public static Guid? CoverAlbumId(this MediaItemDto track) => track.Albums?.FirstOrDefault()?.Id;
}
