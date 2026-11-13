namespace Fossegrim.Lib.Services;

/// <summary>
/// Usernames (Identity's UserName) are unique, ignoring case and surrounding
/// spaces: they're how people sign in and find each other to share
/// playlists, and a copied name could pass someone off as an admin.
/// FossegrimDbContext sets NormalizedUserName, under Identity's unique
/// index, from this whenever a user is saved, however it's saved.
/// </summary>
public static class UserNames
{
    /// <summary>The form names are compared in, or null for no name.</summary>
    public static string? Normalize(string? userName) =>
        string.IsNullOrWhiteSpace(userName) ? null : userName.Trim().ToUpperInvariant();
}
