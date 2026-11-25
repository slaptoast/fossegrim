using System.ComponentModel.DataAnnotations;

namespace Fossegrim.Lib.Models;

/// <summary>
/// A personal access token a user made on their Profile page, for an AI
/// assistant to reach the MCP endpoint (/mcp) as them. Only a SHA-256 hash is
/// stored; Prefix is the token's first characters, kept so people can tell
/// their tokens apart. Tokens work on /mcp only, not on the rest of the API.
/// </summary>
public class AccessToken
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

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
