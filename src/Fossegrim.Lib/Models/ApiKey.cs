using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Lib.Models;

/// <summary>
/// A key other applications use to start library scans (X-Api-Key header).
/// Only a SHA-256 hash of the key is stored; Prefix is its first characters,
/// kept so admins can tell keys apart.
/// </summary>
public class ApiKey
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(16)]
    public string Prefix { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string Hash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }
}
