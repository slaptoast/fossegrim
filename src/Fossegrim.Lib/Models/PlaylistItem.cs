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

    // Fractional position of this track within the playlist, used for ordering.
    // Moving an item only needs to set a value between its new neighbors,
    // so a drag-and-drop reorder writes a single row instead of the whole list.
    public double Position { get; set; }

    public DateTime? DateAdded { get; set; }
}
