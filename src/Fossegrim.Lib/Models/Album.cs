using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Lib.Models;

public class Album
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }
    public string? Genre { get; set; }
    public string? Label { get; set; }
    public string? CoverImageUrl { get; set; }

    // Like Artist.Bio: a Wikipedia summary for the hover card, its page,
    // and when it was last looked up (set with no Summary: nothing found).
    public string? Summary { get; set; }
    public string? InfoUrl { get; set; }
    public DateTime? InfoFetchedAt { get; set; }

    public DateTime? DateAdded { get; set; }
    public DateTime? LastModified { get; set; }

    // Many-to-many relationship with Artists
    public ICollection<Artist> Artists { get; set; } = [];

    // Many-to-many relationship with MediaItems
    public ICollection<MediaItem> MediaItems { get; set; } = [];
}
