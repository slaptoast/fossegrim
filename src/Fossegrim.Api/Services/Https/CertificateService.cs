using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Services.Https;

/// <summary>One certificate request at a time, and a nudge for the worker.</summary>
public class CertificateCoordinator
{
    private readonly SemaphoreSlim _running = new(1, 1);

    public bool IsRunning => _running.CurrentCount == 0;

    public bool TryStart() => _running.Wait(0);

    public void Finished() => _running.Release();
}

/// <summary>
/// HTTPS for a server that faces the internet on its own (a Raspberry Pi,
/// say): gets a Let's Encrypt certificate for the admin's domains, puts it
/// in the folder the proxy serves it from, and renews it once a third of its
/// lifetime is left. Settings, status and the ACME account keys (encrypted
/// with Data Protection) live in AppSettings.
/// </summary>
public partial class CertificateService
{
    public const int MaxDomains = 10;

    // Renew once this much of the lifetime is left: 30 days for today's
    // 90-day certificates, and it keeps working as lifetimes get shorter.
    private const double RenewWhenRemaining = 1.0 / 3;
    private static readonly TimeSpan FirstRetry = TimeSpan.FromHours(1);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromDays(1);

    private const string SettingsKey = "Https:LetsEncrypt";
    private const string StatusKey = "Https:LetsEncryptStatus";
    private const string AccountsKey = "Https:AcmeAccounts";
    private const string RedirectKey = "Https:RedirectHttp";

    private readonly FossegrimDbContext _db;
    private readonly CertificateFiles _files;
    private readonly CertificateCoordinator _coordinator;
    private readonly IAcmeIssuer _issuer;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogger<CertificateService> _logger;

    public CertificateService(
        FossegrimDbContext db,
        CertificateFiles files,
        CertificateCoordinator coordinator,
        IAcmeIssuer issuer,
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider time,
        ILogger<CertificateService> logger)
    {
        _db = db;
        _files = files;
        _coordinator = coordinator;
        _issuer = issuer;
        _protector = dataProtectionProvider.CreateProtector("Fossegrim.Https.AcmeAccountKey");
        _time = time;
        _logger = logger;
    }

    public bool Available => _files.Available;

    public async Task<HttpsStatusDto> GetStatusAsync()
    {
        var settings = await GetSettingsAsync();
        var status = await GetStoredStatusAsync();
        var certificate = _files.Read();
        return new HttpsStatusDto(
            _files.Available,
            certificate,
            settings,
            await GetRedirectAsync(),
            _coordinator.IsRunning,
            status.LastAttemptAt,
            status.LastSuccessAt,
            status.LastError,
            settings is null ? null : NextAttempt(settings, certificate, status));
    }

    public async Task<LetsEncryptSettingsDto?> GetSettingsAsync()
    {
        var json = await ReadAsync(SettingsKey);
        return json is null ? null : JsonSerializer.Deserialize<LetsEncryptSettingsDto>(json);
    }

    /// <summary>The settings a save request would give, or an error saying what's wrong with it.</summary>
    public static (LetsEncryptSettingsDto? Settings, string? Error) Validate(SaveLetsEncryptRequest request)
    {
        if (!request.AgreeToTerms)
        {
            return (null, "Agree to the Let's Encrypt subscriber agreement to get a certificate.");
        }

        var domains = new List<string>();
        foreach (var entered in request.Domains ?? [])
        {
            var trimmed = entered.Trim().TrimEnd('.');
            if (trimmed.Length == 0)
            {
                continue;
            }
            if (NormalizeDomain(trimmed) is not { } domain)
            {
                return (null, $"\"{trimmed}\" isn't a domain name Let's Encrypt can issue for. Enter names like music.example.com (no wildcards, IP addresses or ports).");
            }
            if (!domains.Contains(domain))
            {
                domains.Add(domain);
            }
        }
        if (domains.Count == 0)
        {
            return (null, "Enter the domain name this server is reached on.");
        }
        if (domains.Count > MaxDomains)
        {
            return (null, $"Enter at most {MaxDomains} domain names.");
        }

        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        if (email is not null && !(MimeKit.MailboxAddress.TryParse(email, out var mailbox) && mailbox.Address.Contains('@')))
        {
            return (null, "Enter a valid email address, or leave it blank.");
        }

        return (new LetsEncryptSettingsDto(domains, email, request.Staging), null);
    }

    /// <summary>Saves the settings; the caller then asks for a certificate straight away.</summary>
    public async Task SaveSettingsAsync(LetsEncryptSettingsDto settings)
    {
        await WriteAsync(SettingsKey, JsonSerializer.Serialize(settings));
        // A fresh start: earlier failures were for other settings.
        await SaveStatusAsync((await GetStoredStatusAsync()) with { LastError = null, Failures = 0 });
    }

    /// <summary>Stops using Let's Encrypt and removes the certificate, so the proxy serves plain HTTP only.</summary>
    public async Task TurnOffAsync()
    {
        await _db.AppSettings.Where(s => s.Key == SettingsKey || s.Key == StatusKey).ExecuteDeleteAsync();
        _files.Delete();
        _logger.LogInformation("Let's Encrypt turned off; certificate removed");
    }

    public async Task<bool> GetRedirectAsync()
    {
        var value = await ReadAsync(RedirectKey);
        return !bool.TryParse(value, out var enabled) || enabled;
    }

    /// <summary>Null when saved, otherwise why the proxy couldn't be told.</summary>
    public async Task<string?> SetRedirectAsync(bool enabled)
    {
        try
        {
            _files.SetRedirect(enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't update the HTTP redirect setting in {Directory}", _files.Directory);
            return CantWriteMessage;
        }
        await WriteAsync(RedirectKey, enabled.ToString());
        return null;
    }

    private string CantWriteMessage =>
        $"Fossegrim can't write to the certificate folder ({_files.Directory}). It has to be writable by the container's user (uid 100, gid 101).";

    /// <summary>Makes the proxy's redirect marker match the setting (at startup, e.g. after a restore).</summary>
    public async Task SyncRedirectAsync()
    {
        if (!_files.Available)
        {
            return;
        }
        try
        {
            _files.SetRedirect(await GetRedirectAsync());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't update the HTTP redirect setting in {Directory}", _files.Directory);
        }
    }

    /// <summary>For the worker: requests a certificate when one is due (first issue, renewal, or a retry).</summary>
    public async Task RenewIfDueAsync(CancellationToken cancellationToken)
    {
        if (!_files.Available || await GetSettingsAsync() is not { } settings)
        {
            return;
        }

        var status = await GetStoredStatusAsync();
        if (NextAttempt(settings, _files.Read(), status) is { } due && due > Now)
        {
            return;
        }

        if (!_coordinator.TryStart())
        {
            return;
        }
        try
        {
            await IssueAsync(cancellationToken);
        }
        finally
        {
            _coordinator.Finished();
        }
    }

    /// <summary>
    /// Requests a certificate now. The caller holds the coordinator. Records
    /// the outcome rather than throwing, so the Settings page can show it.
    /// </summary>
    public async Task IssueAsync(CancellationToken cancellationToken)
    {
        if (!_files.Available || await GetSettingsAsync() is not { } settings)
        {
            return;
        }

        var startedAt = Now;
        var status = await GetStoredStatusAsync();
        var directory = LetsEncryptIssuer.DirectoryFor(settings.Staging).ToString();
        try
        {
            var accounts = await GetAccountKeysAsync();
            if (!accounts.TryGetValue(directory, out var accountKey))
            {
                accountKey = accounts[directory] = _issuer.NewAccountKeyPem();
                await SaveAccountKeysAsync(accounts);
            }

            _logger.LogInformation("Requesting a {Kind} certificate for {Domains}",
                settings.Staging ? "staging" : "Let's Encrypt", string.Join(", ", settings.Domains));
            var issued = await _issuer.IssueAsync(
                new AcmeOrder(settings.Domains, settings.Email, settings.Staging, accountKey), cancellationToken);

            _files.Write(issued.FullChainPem, issued.PrivateKeyPem);
            await SyncRedirectAsync();
            await SaveStatusAsync(new StoredStatus(startedAt, Now, null, 0));
            _logger.LogInformation("Certificate for {Domains} saved to {Directory}", string.Join(", ", settings.Domains), _files.Directory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                AcmeException => ex.Message,
                UnauthorizedAccessException => CantWriteMessage,
                _ => $"Getting the certificate failed: {ex.Message}"
            };
            _logger.LogWarning(ex, "Certificate request for {Domains} failed", string.Join(", ", settings.Domains));
            await SaveStatusAsync(status with { LastAttemptAt = startedAt, LastError = message, Failures = status.Failures + 1 });
        }
    }

    /// <summary>
    /// When to ask for a certificate next: now if there isn't one for these
    /// settings, at two thirds of its lifetime if there is, but never before
    /// the retry delay after a failure (doubling from an hour to a day).
    /// </summary>
    internal DateTime NextAttempt(LetsEncryptSettingsDto settings, CertificateInfoDto? certificate, StoredStatus status)
    {
        var due = Covers(certificate, settings)
            ? certificate!.NotAfter - (certificate.NotAfter - certificate.NotBefore) * RenewWhenRemaining
            : DateTime.MinValue;

        if (status is { Failures: > 0, LastAttemptAt: { } lastAttempt })
        {
            var delay = FirstRetry * Math.Pow(2, Math.Min(status.Failures - 1, 10));
            var retryAt = lastAttempt + (delay < LongestRetry ? delay : LongestRetry);
            if (retryAt > due)
            {
                due = retryAt;
            }
        }

        return due == DateTime.MinValue ? Now : due;
    }

    // The certificate in the folder is the one these settings would get.
    private static bool Covers(CertificateInfoDto? certificate, LetsEncryptSettingsDto settings) =>
        certificate is not null &&
        certificate.Staging == settings.Staging &&
        settings.Domains.All(domain => certificate.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase));

    /// <summary>Lower-case ASCII (punycode for international names), or null if it isn't a plain host name.</summary>
    internal static string? NormalizeDomain(string value)
    {
        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(value).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (ascii.Length > 253 || !ascii.Contains('.') || !HostName().IsMatch(ascii) || ascii.EndsWith(".local") ||
            ascii.Split('.')[^1].All(char.IsDigit))
        {
            return null;
        }
        return ascii;
    }

    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex HostName();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    internal record StoredStatus(DateTime? LastAttemptAt, DateTime? LastSuccessAt, string? LastError, int Failures);

    private async Task<StoredStatus> GetStoredStatusAsync() =>
        await ReadAsync(StatusKey) is { } json
            ? JsonSerializer.Deserialize<StoredStatus>(json)!
            : new StoredStatus(null, null, null, 0);

    private Task SaveStatusAsync(StoredStatus status) => WriteAsync(StatusKey, JsonSerializer.Serialize(status));

    // ACME directory URL -> account key PEM. Staging and production have separate accounts.
    private async Task<Dictionary<string, string>> GetAccountKeysAsync()
    {
        var keys = new Dictionary<string, string>();
        if (await ReadAsync(AccountsKey) is not { } json)
        {
            return keys;
        }

        foreach (var (directory, protectedKey) in JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [])
        {
            try
            {
                keys[directory] = _protector.Unprotect(protectedKey);
            }
            catch (CryptographicException ex)
            {
                // The Data Protection keys were lost; a new account is made, which is harmless.
                _logger.LogWarning(ex, "Could not decrypt the saved ACME account key for {Directory}; a new account will be registered", directory);
            }
        }
        return keys;
    }

    private Task SaveAccountKeysAsync(Dictionary<string, string> keys) =>
        WriteAsync(AccountsKey, JsonSerializer.Serialize(keys.ToDictionary(k => k.Key, k => _protector.Protect(k.Value))));

    private Task<string?> ReadAsync(string key) =>
        _db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync();

    private async Task WriteAsync(string key, string value)
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
        await _db.SaveChangesAsync();
    }
}

/// <summary>
/// Checks every hour whether the certificate needs getting or renewing.
/// Turned off with Https:WorkerEnabled = false (tests).
/// </summary>
public class CertificateWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CertificateWorker> _logger;

    public CertificateWorker(IServiceScopeFactory scopeFactory, ILogger<CertificateWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<CertificateService>().RenewIfDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Certificate renewal check failed; trying again in {Interval}", Interval);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
