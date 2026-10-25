using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Lib.Models;

/// <summary>
/// A server-wide setting edited from the admin Settings page, stored as a
/// key/value pair. Secret values are encrypted by the caller before saving.
/// </summary>
public class AppSetting
{
    [Key]
    [MaxLength(200)]
    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
