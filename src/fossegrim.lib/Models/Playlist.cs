using System.ComponentModel.DataAnnotations;
using Fossegrim.Lib.Enums;

namespace Fossegrim.Lib.Models;

public class Playlist
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public PlaylistType Type { get; set; } = PlaylistType.Standard;

    // The user who created the playlist. Owners always have full access;
    // additional access is granted via Shares.
    [Required]
    public string OwnerId { get; set; } = string.Empty;
    public ApplicationUser? Owner { get; set; }

    public DateTime? DateAdded { get; set; }
    public DateTime? LastModified { get; set; }

    // The track currently "now playing" for this playlist. Only meaningful
    // for a Queue playlist, which tracks playback position across devices.
    public Guid? CurrentMediaItemId { get; set; }
    public MediaItem? CurrentMediaItem { get; set; }

    // Ordered tracks (by MediaItem reference) that make up this playlist
    public ICollection<PlaylistItem> Items { get; set; } = [];

    // Other users this playlist has been shared with, and their access level
    public ICollection<PlaylistShare> Shares { get; set; } = [];
}
