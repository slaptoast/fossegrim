using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Lib.Models;

public class Artist
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Bio { get; set; }
    public string? Country { get; set; }
    public string? ImageUrl { get; set; }

    public DateTime? DateAdded { get; set; }
    public DateTime? LastModified { get; set; }

    // Many-to-many relationship with MediaItems
    public ICollection<MediaItem> MediaItems { get; set; } = [];

    // Many-to-many relationship with Albums
    public ICollection<Album> Albums { get; set; } = [];
}
