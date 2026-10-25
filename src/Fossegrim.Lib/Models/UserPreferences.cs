using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Lib.Models;

/// <summary>
/// One user's appearance settings (theme, custom themes, page layouts), kept
/// on the server so they follow the user across browsers. Stored as a JSON
/// document the web client owns the shape of; the server only checks it's a
/// JSON object of reasonable size.
/// </summary>
public class UserPreferences
{
    [Key]
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    [Required]
    public string Json { get; set; } = "{}";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
