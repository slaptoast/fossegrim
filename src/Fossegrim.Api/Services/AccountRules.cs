using Fossegrim.Lib.Data;
using Fossegrim.Lib.Services;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Services;

/// <summary>
/// Checks on what users can call themselves, with messages for the caller.
/// Usernames are unique (see <see cref="UserNames"/>); the database
/// enforces that too, but checking first gives a readable error.
/// </summary>
public static class AccountRules
{
    public const int MaxUserNameLength = 100;
    public const int MaxPhoneNumberLength = 30;

    /// <summary>The username trimmed, or null when there isn't one.</summary>
    public static string? Clean(string? userName) =>
        string.IsNullOrWhiteSpace(userName) ? null : userName.Trim();

    /// <summary>Why <paramref name="userName"/> can't be used, or null when it's fine.</summary>
    public static async Task<string?> CheckUserNameAsync(FossegrimDbContext db, string? userName, string? userId = null)
    {
        if (Clean(userName) is not { } name)
        {
            return "Choose a username.";
        }
        if (name.Length > MaxUserNameLength)
        {
            return $"Usernames can be at most {MaxUserNameLength} characters.";
        }

        var normalized = UserNames.Normalize(name);
        var taken = await db.Users.AnyAsync(u => u.Id != userId && u.NormalizedUserName == normalized);
        return taken ? "Someone else already uses that username." : null;
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
