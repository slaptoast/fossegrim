using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Lib.Models
{
    public class MediaItem
    {
        [Key]
        public Guid Id { get; set; }

        public string? Title { get; set; }

        // Keep the string artist field for backward compatibility and fallback
        public string? ArtistName { get; set; }

        public string? Album { get; set; }

        [Required]
        public string? FileLocation { get; set; }

        public DateTime? DateAdded { get; set; }
        public DateTime? LastModified { get; set; }

        // Additional metadata
        public int? Year { get; set; }
        public int? Track { get; set; }
        public string? Genre { get; set; }
        public TimeSpan? Duration { get; set; }
        public int? Bitrate { get; set; }

        // The file's size and last-write time when it was last read, so a
        // quick scan can skip files that haven't changed.
        public long? FileSize { get; set; }
        public DateTime? FileModifiedAt { get; set; }

        // Comma-separated tag fields where the file's ID3v1 and ID3v2 tags
        // disagree (e.g. "title,year"); null when they agree.
        [MaxLength(100)]
        public string? TagVersionMismatch { get; set; }

        // Foreign key to the folder this media item was found in
        public Guid? MediaFolderId { get; set; }
        public MediaFolder? MediaFolder { get; set; }

        // Many-to-many relationship with Artists
        public ICollection<Artist> Artists { get; set; } = [];

        // Many-to-many relationship with Albums
        public ICollection<Album> Albums { get; set; } = [];
    }
}