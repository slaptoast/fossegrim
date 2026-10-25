using System.Security.Cryptography;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace Fossegrim.Api.Services;

public record LastFmAccount(string SessionKey, string? UserName);

/// <summary>
/// Stores each user's linked Last.fm account in Identity's AspNetUserTokens
/// table (so it's deleted along with the user, and needs no migration).
/// Session keys never expire, so they're encrypted with Data Protection;
/// the Last.fm username is kept in plain text for display.
/// </summary>
public class LastFmAccountStore
{
    private const string LoginProvider = "LastFm";
    private const string SessionKeyName = "SessionKey";
    private const string UserNameName = "UserName";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDataProtector _protector;
    private readonly ILogger<LastFmAccountStore> _logger;

    public LastFmAccountStore(
        UserManager<ApplicationUser> userManager,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<LastFmAccountStore> logger)
    {
        _userManager = userManager;
        _protector = dataProtectionProvider.CreateProtector("Fossegrim.LastFm.SessionKey");
        _logger = logger;
    }

    public async Task<LastFmAccount?> GetAsync(ApplicationUser user)
    {
        var protectedKey = await _userManager.GetAuthenticationTokenAsync(user, LoginProvider, SessionKeyName);
        if (string.IsNullOrEmpty(protectedKey))
        {
            return null;
        }

        string sessionKey;
        try
        {
            sessionKey = _protector.Unprotect(protectedKey);
        }
        catch (CryptographicException ex)
        {
            // The Data Protection keys were lost or replaced; the user has to link again.
            _logger.LogWarning(ex, "Could not decrypt the Last.fm session key for user {UserId}", user.Id);
            return null;
        }

        var userName = await _userManager.GetAuthenticationTokenAsync(user, LoginProvider, UserNameName);
        return new LastFmAccount(sessionKey, userName);
    }

    public async Task SetAsync(ApplicationUser user, string sessionKey, string? userName)
    {
        await _userManager.SetAuthenticationTokenAsync(user, LoginProvider, SessionKeyName, _protector.Protect(sessionKey));
        if (string.IsNullOrEmpty(userName))
        {
            await _userManager.RemoveAuthenticationTokenAsync(user, LoginProvider, UserNameName);
        }
        else
        {
            await _userManager.SetAuthenticationTokenAsync(user, LoginProvider, UserNameName, userName);
        }
    }

    public async Task RemoveAsync(ApplicationUser user)
    {
        await _userManager.RemoveAuthenticationTokenAsync(user, LoginProvider, SessionKeyName);
        await _userManager.RemoveAuthenticationTokenAsync(user, LoginProvider, UserNameName);
    }
}
