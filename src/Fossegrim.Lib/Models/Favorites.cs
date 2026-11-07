namespace Fossegrim.Lib.Models;

// A user's favourite tracks, albums and artists (Subsonic's "starred"). Each
// is removed along with the user or the item, so a scan that drops a track
// drops it from favourites too.

public class FavoriteTrack
{
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class FavoriteAlbum
{
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public Guid AlbumId { get; set; }
    public Album? Album { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class FavoriteArtist
{
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public Guid ArtistId { get; set; }
    public Artist? Artist { get; set; }
    public DateTime CreatedAt { get; set; }
}
