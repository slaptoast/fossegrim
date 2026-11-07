using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Lib.Models;

/// <summary>
/// One signed-in app or browser. Its access tokens carry the session id, so
/// deleting the session signs that device out at once. Sessions don't
/// expire: the app swaps its refresh token for new tokens for as long as it
/// likes, and only signing out (or a password change) ends it.
/// </summary>
public class UserSession
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    // What the app called itself when signing in, e.g. "Fossegrim Web".
    [MaxLength(100)]
    public string? ClientName { get; set; }

    [MaxLength(512)]
    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime LastUsedAt { get; set; }

    // SHA-256 (hex) of the current refresh token. Each refresh replaces it.
    [Required]
    [MaxLength(64)]
    public string RefreshTokenHash { get; set; } = string.Empty;

    // The token the current one replaced. It still works until the current
    // one is used, so an app whose refresh response got lost can retry.
    [MaxLength(64)]
    public string? PreviousRefreshTokenHash { get; set; }
}
