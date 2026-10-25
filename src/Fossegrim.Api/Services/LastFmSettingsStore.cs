using System.Security.Cryptography;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fossegrim.Api.Services;

/// <summary>
/// Decides which Last.fm API credentials are in effect and publishes them to
/// <see cref="LastFmCredentialsHolder"/>: LastFm:ApiKey/ApiSecret from
/// configuration (env vars) win when both are set; otherwise the ones an
/// admin saved on the Settings page, whose secret is encrypted with Data
/// Protection in the AppSettings table.
/// </summary>
public class LastFmSettingsStore
{
    private const string ApiKeySetting = "LastFm:ApiKey";
    private const string ApiSecretSetting = "LastFm:ApiSecret";

    private readonly FossegrimDbContext _db;
    private readonly IDataProtector _protector;
    private readonly LastFmOptions _options;
    private readonly LastFmCredentialsHolder _holder;
    private readonly ILogger<LastFmSettingsStore> _logger;

    public LastFmSettingsStore(
        FossegrimDbContext db,
        IDataProtectionProvider dataProtectionProvider,
        IOptions<LastFmOptions> options,
        LastFmCredentialsHolder holder,
        ILogger<LastFmSettingsStore> logger)
    {
        _db = db;
        _protector = dataProtectionProvider.CreateProtector("Fossegrim.LastFm.ApiSecret");
        _options = options.Value;
        _holder = holder;
        _logger = logger;
    }

    public bool IsSetByConfiguration => _options.IsConfigured;

    // The saved key, and whether a secret is saved (the secret itself is
    // never handed back out).
    public async Task<(string? ApiKey, bool HasSecret)> GetSavedAsync()
    {
        var settings = await LoadSettingsAsync();
        return (settings.GetValueOrDefault(ApiKeySetting), !string.IsNullOrEmpty(settings.GetValueOrDefault(ApiSecretSetting)));
    }

    // Null when nothing is saved or the saved secret can't be decrypted.
    public async Task<string?> GetSavedSecretAsync()
    {
        var settings = await LoadSettingsAsync();
        return Unprotect(settings.GetValueOrDefault(ApiSecretSetting));
    }

    /// <summary>Recomputes the credentials in effect; call at startup.</summary>
    public async Task LoadAsync()
    {
        if (_options.IsConfigured)
        {
            _holder.Set(new LastFmCredentials(_options.ApiKey!, _options.ApiSecret!, LastFmCredentialSource.Configuration));
            return;
        }

        var settings = await LoadSettingsAsync();
        var apiKey = settings.GetValueOrDefault(ApiKeySetting);
        var apiSecret = Unprotect(settings.GetValueOrDefault(ApiSecretSetting));
        _holder.Set(!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(apiSecret)
            ? new LastFmCredentials(apiKey, apiSecret, LastFmCredentialSource.Settings)
            : null);
    }

    public async Task SaveAsync(string apiKey, string apiSecret)
    {
        await UpsertAsync(ApiKeySetting, apiKey);
        await UpsertAsync(ApiSecretSetting, _protector.Protect(apiSecret));
        await _db.SaveChangesAsync();
        await LoadAsync();
    }

    public async Task ClearAsync()
    {
        await _db.AppSettings
            .Where(s => s.Key == ApiKeySetting || s.Key == ApiSecretSetting)
            .ExecuteDeleteAsync();
        await LoadAsync();
    }

    private async Task<Dictionary<string, string?>> LoadSettingsAsync() =>
        await _db.AppSettings
            .AsNoTracking()
            .Where(s => s.Key == ApiKeySetting || s.Key == ApiSecretSetting)
            .ToDictionaryAsync(s => s.Key, s => s.Value);

    private async Task UpsertAsync(string key, string value)
    {
        var setting = await _db.AppSettings.FindAsync(key);
        if (setting is null)
        {
            _db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTime.UtcNow;
        }
    }

    private string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException ex)
        {
            // The Data Protection keys were lost or replaced; an admin has to re-enter the secret.
            _logger.LogWarning(ex, "Could not decrypt the saved Last.fm API secret; Last.fm is off until it's re-entered on the Settings page");
            return null;
        }
    }
}
