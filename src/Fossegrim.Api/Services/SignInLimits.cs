using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;

namespace Fossegrim.Api.Services;

/// <summary>
/// Limits on guessing passwords: the rate limiter policy on the sign-in
/// endpoints (per client address, RateLimiting:SignInPerMinute) and account
/// lockout after repeated wrong passwords.
/// </summary>
public static class SignInLimits
{
    public const string Policy = "sign-in";
    public const int DefaultPerMinute = 20;
    public const int MaxFailedAttempts = 10;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    // Checked when the email matches no account, so a wrong email takes as
    // long as a wrong password and response times don't reveal which
    // addresses have accounts.
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), Guid.NewGuid().ToString()));

    public static void SpendTimeOnUnknownUser(IPasswordHasher<ApplicationUser> hasher, string? password) =>
        hasher.VerifyHashedPassword(new ApplicationUser(), DummyHash.Value, password ?? "");
}
