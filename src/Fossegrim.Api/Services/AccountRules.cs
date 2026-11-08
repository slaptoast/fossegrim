using Fossegrim.Lib.Data;
using Fossegrim.Lib.Services;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Services;

/// <summary>
/// Checks on what users can call themselves, with messages for the caller.
/// Display names are unique (see <see cref="DisplayNames"/>); the database
/// enforces that too, but checking first gives a readable error.
/// </summary>
public static class AccountRules
{
    public const int MaxDisplayNameLength = 100;
    public const int MaxPhoneNumberLength = 30;

    /// <summary>The display name trimmed, or null when there isn't one.</summary>
    public static string? Clean(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

    /// <summary>Why <paramref name="displayName"/> can't be used, or null when it's fine.</summary>
    public static async Task<string?> CheckDisplayNameAsync(FossegrimDbContext db, string? displayName, string? userId = null)
    {
        if (Clean(displayName) is not { } name)
        {
            return null;
        }
        if (name.Length > MaxDisplayNameLength)
        {
            return $"Display names can be at most {MaxDisplayNameLength} characters.";
        }

        var normalized = DisplayNames.Normalize(name);
        var taken = await db.Users.AnyAsync(u => u.Id != userId && u.NormalizedDisplayName == normalized);
        return taken ? "Someone else already uses that display name." : null;
    }

    public static string? CheckPhoneNumber(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return null;
        }
        if (phoneNumber.Length > MaxPhoneNumberLength)
        {
            return $"Phone numbers can be at most {MaxPhoneNumberLength} characters.";
        }
        return phoneNumber.All(c => char.IsAsciiDigit(c) || c is ' ' or '+' or '-' or '(' or ')' or '.')
            ? null
            : "Phone numbers can only contain digits, spaces and + - ( ) .";
    }
}
