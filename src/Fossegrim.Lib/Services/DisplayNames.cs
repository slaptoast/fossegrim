using Fossegrim.Lib.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fossegrim.Lib.Services;

/// <summary>
/// Display names are unique, ignoring case and surrounding spaces: they're
/// how people find each other to share playlists, and a copied name could
/// pass someone off as an admin. ApplicationUser.NormalizedDisplayName holds
/// the comparable form, under a unique index; FossegrimDbContext fills it in
/// whenever a user is saved.
/// </summary>
public static class DisplayNames
{
    /// <summary>The form names are compared in, or null for no name.</summary>
    public static string? Normalize(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim().ToUpperInvariant();

    /// <summary>
    /// Fills in the normalized names of users saved before they existed.
    /// Where names clash, the account that joined first keeps its name and
    /// the others get a number ("Tom 2"), which they can change on their
    /// Profile page. Call at startup.
    /// </summary>
    public static async Task EnsureUniqueAsync(FossegrimDbContext db, ILogger logger)
    {
        var users = await db.Users.OrderBy(u => u.DateJoined).ThenBy(u => u.Id).ToListAsync();
        if (users.All(u => u.NormalizedDisplayName == Normalize(u.DisplayName)))
        {
            return;
        }

        // Clear first so the index can't trip over a half-done pass.
        await db.Users.ExecuteUpdateAsync(s => s.SetProperty(u => u.NormalizedDisplayName, (string?)null));

        // First the earliest holder of each name keeps it, so the numbers
        // given out below never take a name someone already has.
        var taken = new HashSet<string>();
        var duplicates = new List<Models.ApplicationUser>();
        foreach (var user in users)
        {
            if (Normalize(user.DisplayName) is not { } normalized)
            {
                user.DisplayName = null;
            }
            else if (taken.Add(normalized))
            {
                user.DisplayName = user.DisplayName!.Trim();
            }
            else
            {
                duplicates.Add(user);
            }
        }

        foreach (var user in duplicates)
        {
            var original = user.DisplayName!.Trim();
            var name = original;
            for (var n = 2; !taken.Add(Normalize(name = $"{original} {n}")!); n++)
            {
            }
            logger.LogWarning("Display name {Original} was already taken; renamed user {UserId} to {Renamed}", original, user.Id, name);
            user.DisplayName = name;
        }

        // SaveChanges sets each NormalizedDisplayName from its DisplayName.
        foreach (var entry in db.ChangeTracker.Entries<Models.ApplicationUser>())
        {
            entry.Property(u => u.NormalizedDisplayName).IsModified = true;
        }
        await db.SaveChangesAsync();
    }
}
