using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Lib.Models
{
    public class MediaFolder
    {
        public MediaFolder()
        {
            Id = Guid.NewGuid();
            Name = string.Empty;
            Location = string.Empty;
        }

        [Key]
        public Guid Id { get; set; }

        [Required]
        public string Name { get; set; }

        [Required]
        public string Location { get; set; }

        public DateTime? DateAdded { get; set; }
        public DateTime? DateLastScanned { get; set; }

        // Navigation property for media items found in this folder
        public ICollection<MediaItem> MediaItems { get; set; } = [];
    }
}