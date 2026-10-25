using Microsoft.AspNetCore.Identity;

namespace Fossegrim.Lib.Models;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public DateTime DateJoined { get; set; } = DateTime.UtcNow;
}
