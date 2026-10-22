using System.ComponentModel.DataAnnotations;
using Fossegrim.Lib.Enums;

namespace Fossegrim.Lib.Models;

public class PlaylistShare
{
    [Key]
    public Guid Id { get; set; }

    public Guid PlaylistId { get; set; }
    public Playlist? Playlist { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public PlaylistShareType ShareType { get; set; } = PlaylistShareType.ReadOnly;

    public DateTime? DateAdded { get; set; }
}
