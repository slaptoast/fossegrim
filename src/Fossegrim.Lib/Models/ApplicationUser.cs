using Microsoft.AspNetCore.Identity;

namespace Fossegrim.Lib.Models;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    // DisplayName trimmed and upper-cased, unique (see DisplayNames). Set on save.
    public string? NormalizedDisplayName { get; set; }
    public DateTime DateJoined { get; set; } = DateTime.UtcNow;
}
