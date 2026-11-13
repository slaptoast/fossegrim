using Microsoft.AspNetCore.Identity;

namespace Fossegrim.Lib.Models;

public class ApplicationUser : IdentityUser
{
    public DateTime DateJoined { get; set; } = DateTime.UtcNow;
    // Set when an admin deletes the account. Everything the account owns
    // stays, so restoring it (clearing this) brings it back as it was.
    // A deleted account can't sign in and isn't listed or searchable; it
    // keeps its email and username, so neither can be reused meanwhile.
    public DateTime? DeletedAt { get; set; }
}
