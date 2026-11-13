using System.Collections.Concurrent;
using System.Text;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace Fossegrim.Api.Services;

/// <summary>
/// Reset links: "/reset-password?user=…&amp;token=…", emailed from "Forgot
/// password?", made by an admin for someone, or printed by the
/// reset-password command. The token is Identity's password reset token; it
/// carries the account's security stamp, so it stops working as soon as the
/// password changes (one use) and expires after <see cref="LinkLifetime"/>.
/// </summary>
public class PasswordResets
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24);
    public const string InvalidLinkMessage = "This reset link has expired or has already been used. Ask for a new one.";

    private readonly UserManager<ApplicationUser> _users;
    private readonly EmailSettingsStore _emailSettings;
    private readonly EmailQueue _emails;
    private readonly ResetEmailThrottle _throttle;
    private readonly SessionService _sessions;
    private readonly TimeProvider _time;
    private readonly ILogger<PasswordResets> _logger;

    public PasswordResets(
        UserManager<ApplicationUser> users,
        EmailSettingsStore emailSettings,
        EmailQueue emails,
        ResetEmailThrottle throttle,
        SessionService sessions,
        TimeProvider time,
        ILogger<PasswordResets> logger)
    {
        _users = users;
        _emailSettings = emailSettings;
        _emails = emails;
        _throttle = throttle;
        _sessions = sessions;
        _time = time;
        _logger = logger;
    }

    public async Task<PasswordResetLinkDto> CreateLinkAsync(ApplicationUser user)
    {
        var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await _users.GeneratePasswordResetTokenAsync(user)));
        return new PasswordResetLinkDto(
            $"/reset-password?user={Uri.EscapeDataString(user.Id)}&token={token}",
            _time.GetUtcNow().UtcDateTime + LinkLifetime);
    }

    /// <summary>
    /// Emails a reset link if the address belongs to an account. Says
    /// nothing either way, so it can't be used to find out who has one; at
    /// most one email per account every few minutes.
    /// </summary>
    public async Task RequestByEmailAsync(string email, SmtpSettings settings)
    {
        if (string.IsNullOrWhiteSpace(email) || await _users.FindByEmailAsync(email.Trim()) is not { Email: not null, DeletedAt: null } user)
        {
            return;
        }
        if (!_throttle.TryStart(user.Id))
        {
            _logger.LogInformation("Skipped a password reset email to user {UserId}: one was sent a few minutes ago", user.Id);
            return;
        }

        var link = await CreateLinkAsync(user);
        var name = string.IsNullOrWhiteSpace(user.UserName) ? "there" : user.UserName;
        _emails.Enqueue(new OutgoingEmail(user.Email, "Reset your Fossegrim password", $"""
            Hi {name},

            Someone (hopefully you) asked to reset the password for your Fossegrim account. To choose a new one, open this link:

            {settings.PublicUrl}{link.Path}

            It works once, for the next {LinkLifetime.TotalHours:0} hours. If you didn't ask for this, ignore this email and your password stays as it is.
            """));
    }

    /// <summary>
    /// Sets a new password from a reset link. Also unlocks the account and
    /// signs it out everywhere. Null on success, otherwise the errors to show.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ResetAsync(ResetPasswordRequest request)
    {
        if (string.IsNullOrEmpty(request.UserId) || await _users.FindByIdAsync(request.UserId) is not { DeletedAt: null } user)
        {
            return [InvalidLinkMessage];
        }

        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Token ?? ""));
        }
        catch (FormatException)
        {
            return [InvalidLinkMessage];
        }

        var result = await _users.ResetPasswordAsync(user, token, request.NewPassword ?? "");
        if (!result.Succeeded)
        {
            var invalidToken = _users.ErrorDescriber.InvalidToken().Code;
            return result.Errors.Any(e => e.Code == invalidToken)
                ? [InvalidLinkMessage]
                : result.Errors.Select(e => e.Description).ToList();
        }

        await _users.SetLockoutEndDateAsync(user, null);
        await _users.ResetAccessFailedCountAsync(user);
        await _sessions.RevokeOthersAsync(user.Id, keep: null);
        _logger.LogInformation("User {UserId} reset their password with a reset link", user.Id);
        return null;
    }
}

/// <summary>At most one "Forgot password?" email per account in <see cref="Interval"/>, so the form can't be used to flood someone's inbox.</summary>
public class ResetEmailThrottle
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();
    private readonly TimeProvider _time;

    public ResetEmailThrottle(TimeProvider time)
    {
        _time = time;
    }

    public bool TryStart(string userId)
    {
        var now = _time.GetUtcNow();
        foreach (var (key, sent) in _lastSent)
        {
            if (now - sent >= Interval)
            {
                _lastSent.TryRemove(key, out _);
            }
        }
        return _lastSent.TryAdd(userId, now);
    }
}
