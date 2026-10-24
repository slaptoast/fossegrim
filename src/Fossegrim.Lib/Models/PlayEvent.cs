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

    // When the track started playing (UTC). Reported by the client, since a
    // play is only reported once enough of it has been heard (and Last.fm
    // scrobbles need the start time); the server's time is used if omitted.
    public DateTime PlayedAt { get; set; }

    // How long the user actually listened, not counting time skipped by
    // seeking. Null when the client didn't report it.
    public TimeSpan? ListenedFor { get; set; }

    // Stamped by the server (UTC) when the play is recorded.
    public DateTime RecordedAt { get; set; }

    // When this play was sent to Last.fm; null until then, and stays null
    // for users who haven't linked Last.fm.
    public DateTime? ScrobbledAt { get; set; }
}
