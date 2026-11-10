using System.Security.Cryptography;
using System.Text.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Services;

/// <summary>The outgoing mail server, with its password decrypted, ready to send with.</summary>
public record SmtpSettings(
    string Host,
    int Port,
    string Security,
    string? UserName,
    string? Password,
    string FromAddress,
    string? FromName,
    string PublicUrl);

/// <summary>
/// The SMTP server an admin saved on the Settings page, as one JSON value in
/// AppSettings. The password is encrypted with Data Protection, like the
/// Last.fm secret. Without saved settings Fossegrim sends no email, and
/// "Forgot password?" tells people to ask an admin.
/// </summary>
public class EmailSettingsStore
{
    private const string Setting = "Email:Smtp";

    private readonly FossegrimDbContext _db;
    private readonly IDataProtector _protector;
    private readonly ILogger<EmailSettingsStore> _logger;

    public EmailSettingsStore(FossegrimDbContext db, IDataProtectionProvider dataProtectionProvider, ILogger<EmailSettingsStore> logger)
    {
        _db = db;
        _protector = dataProtectionProvider.CreateProtector("Fossegrim.Email.SmtpPassword");
        _logger = logger;
    }

    public async Task<bool> IsConfiguredAsync() => await LoadStoredAsync() is not null;

    /// <summary>Null when nothing is saved.</summary>
    public async Task<SmtpSettings?> GetAsync()
    {
        if (await LoadStoredAsync() is not { } stored)
        {
            return null;
        }
        return new SmtpSettings(stored.Host, stored.Port, stored.Security, stored.UserName, Unprotect(stored.Password),
            stored.FromAddress, stored.FromName, stored.PublicUrl);
    }

    public async Task<EmailSettingsDto> GetDtoAsync() =>
        await LoadStoredAsync() is { } s
            ? new EmailSettingsDto(true, s.Host, s.Port, s.Security, s.UserName, !string.IsNullOrEmpty(s.Password), s.FromAddress, s.FromName, s.PublicUrl)
            : new EmailSettingsDto(false, null, 587, SmtpSecurity.StartTls, null, false, null, null, null);

    /// <summary>The settings a save request would give, with the saved password when the request leaves it out.</summary>
    public async Task<SmtpSettings> ResolveAsync(SaveEmailSettingsRequest request)
    {
        var userName = string.IsNullOrWhiteSpace(request.UserName) ? null : request.UserName.Trim();
        // No username means no sign-in, so no password to keep either.
        var password = userName is null ? null : request.Password ?? (await GetAsync())?.Password;
        return new SmtpSettings(
            (request.Host ?? "").Trim(),
            request.Port,
            request.Security,
            userName,
            string.IsNullOrEmpty(password) ? null : password,
            (request.FromAddress ?? "").Trim(),
            string.IsNullOrWhiteSpace(request.FromName) ? null : request.FromName.Trim(),
            (request.PublicUrl ?? "").Trim().TrimEnd('/'));
    }

    public async Task SaveAsync(SmtpSettings settings)
    {
        var stored = new Stored(settings.Host, settings.Port, settings.Security, settings.UserName,
            settings.Password is null ? null : _protector.Protect(settings.Password),
            settings.FromAddress, settings.FromName, settings.PublicUrl);
        var json = JsonSerializer.Serialize(stored);

        var setting = await _db.AppSettings.FindAsync(Setting);
        if (setting is null)
        {
            _db.AppSettings.Add(new AppSetting { Key = Setting, Value = json });
        }
        else
        {
            setting.Value = json;
            setting.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    public Task ClearAsync() => _db.AppSettings.Where(s => s.Key == Setting).ExecuteDeleteAsync();

    /// <summary>Null when the settings are fine, otherwise what's wrong with them.</summary>
    public static string? Validate(SmtpSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            return "Enter the mail server's host name.";
        }
        if (settings.Port is < 1 or > 65535)
        {
            return "The port must be between 1 and 65535.";
        }
        if (!SmtpSecurity.All.Contains(settings.Security))
        {
            return $"Security must be one of {string.Join(", ", SmtpSecurity.All)}.";
        }
        if (!MimeKit.MailboxAddress.TryParse(settings.FromAddress, out var from) || !from.Address.Contains('@'))
        {
            return "Enter the address emails are sent from.";
        }
        if (!Uri.TryCreate(settings.PublicUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return "Enter the address people open Fossegrim on, starting with https:// or http://.";
        }
        return null;
    }

    private async Task<Stored?> LoadStoredAsync()
    {
        var json = await _db.AppSettings.AsNoTracking().Where(s => s.Key == Setting).Select(s => s.Value).FirstOrDefaultAsync();
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Stored>(json);
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
            // The Data Protection keys were lost or replaced; an admin has to re-enter the password.
            _logger.LogWarning(ex, "Could not decrypt the saved SMTP password; re-enter it on the Settings page");
            return null;
        }
    }

    // Password is the protected (encrypted) form.
    private record Stored(string Host, int Port, string Security, string? UserName, string? Password, string FromAddress, string? FromName, string PublicUrl);
}
