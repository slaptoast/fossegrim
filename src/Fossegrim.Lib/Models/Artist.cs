using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Lib.Models;

public class Artist
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    // A short Wikipedia summary (found through MusicBrainz) for the hover
    // card, its page, and when it was last looked up: InfoFetchedAt set
    // with no Bio means nothing was found, so it isn't asked again soon.
    public string? Bio { get; set; }
    public string? Country { get; set; }
    public string? ImageUrl { get; set; }
    public string? InfoUrl { get; set; }
    public DateTime? InfoFetchedAt { get; set; }

    public DateTime? DateAdded { get; set; }
    public DateTime? LastModified { get; set; }

    // Many-to-many relationship with MediaItems
    public ICollection<MediaItem> MediaItems { get; set; } = [];

    // Many-to-many relationship with Albums
    public ICollection<Album> Albums { get; set; } = [];
}
