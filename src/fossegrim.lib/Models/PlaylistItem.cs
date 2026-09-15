using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Lib.Models;

public class PlaylistItem
{
    [Key]
    public Guid Id { get; set; }

    public Guid PlaylistId { get; set; }
    public Playlist? Playlist { get; set; }

    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    // Position of this track within the playlist, used for ordering
    public int Position { get; set; }

    public DateTime? DateAdded { get; set; }
}
