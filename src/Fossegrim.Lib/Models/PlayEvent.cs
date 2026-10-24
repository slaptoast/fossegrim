using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Lib.Models;

// A single entry in a user's listening history: one track played by one user
// at one point in time. When a client reports a play is decided client-side.
public class PlayEvent
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    // Stamped by the server (UTC) when the play is recorded.
    public DateTime PlayedAt { get; set; }
}
