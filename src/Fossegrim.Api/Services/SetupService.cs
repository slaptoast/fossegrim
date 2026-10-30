using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Services;

/// <summary>
/// First-run state and the server-wide account settings the setup wizard
/// manages, stored in AppSettings. A fresh install has no users: the wizard's
/// first step creates the admin (the only thing it can do anonymously) and
/// marks setup in progress; finishing the wizard marks it complete.
/// </summary>
public class SetupService
{
    private const string CompletedAtSetting = "Setup:CompletedAt";
    private const string InProgressSetting = "Setup:InProgress";
    private const string RegistrationOpenSetting = "Registration:Open";

    // Two people racing to claim a fresh install mustn't both become admin.
    private static readonly SemaphoreSlim FirstAdminLock = new(1, 1);

    private readonly FossegrimDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly TimeProvider _time;

    public SetupService(FossegrimDbContext db, UserManager<ApplicationUser> users, TimeProvider time)
    {
        _db = db;
        _users = users;
        _time = time;
    }

    public async Task<SetupStatusDto> GetStatusAsync() => new(
        await IsSetupRequiredAsync(),
        await GetSettingAsync(CompletedAtSetting) is not null,
        await IsRegistrationOpenAsync());

    public async Task<bool> IsSetupRequiredAsync() => !await _db.Users.AnyAsync();

    /// <summary>
    /// Installs from before the wizard already have users and were set up by
    /// hand, so they shouldn't be sent through it. Call at startup.
    /// </summary>
    public async Task MarkExistingInstallCompleteAsync()
    {
        if (await _db.Users.AnyAsync() &&
            await GetSettingAsync(CompletedAtSetting) is null &&
            await GetSettingAsync(InProgressSetting) is null)
        {
            await MarkCompleteAsync();
        }
    }

    /// <summary>Creates the first account, as an admin; fails once any account exists.</summary>
    public async Task<(ApplicationUser? User, IdentityResult Result)> CreateFirstAdminAsync(CreateFirstAdminRequest request)
    {
        await FirstAdminLock.WaitAsync();
        try
        {
            if (!await IsSetupRequiredAsync())
            {
                return (null, IdentityResult.Failed(new IdentityError { Description = "Setup has already been done." }));
            }

            var (user, result) = await CreateUserAsync(request.Email, request.DisplayName, request.Password, isAdmin: true);
            if (result.Succeeded)
            {
                await SetSettingAsync(InProgressSetting, "true");
            }
            return (user, result);
        }
        finally
        {
            FirstAdminLock.Release();
        }
    }

    public async Task<(ApplicationUser? User, IdentityResult Result)> CreateUserAsync(string email, string displayName, string password, bool isAdmin)
    {
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            DateJoined = _time.GetUtcNow().UtcDateTime,
            EmailConfirmed = true
        };

        var result = await _users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            return (null, result);
        }

        if (isAdmin)
        {
            result = await _users.AddToRoleAsync(user, "Admin");
        }
        return (user, result);
    }

    public async Task MarkCompleteAsync()
    {
        await SetSettingAsync(CompletedAtSetting, _time.GetUtcNow().UtcDateTime.ToString("O"));
        await _db.AppSettings.Where(s => s.Key == InProgressSetting).ExecuteDeleteAsync();
    }

    // Closed unless an admin opens it; admins can always add accounts.
    public async Task<bool> IsRegistrationOpenAsync() =>
        bool.TryParse(await GetSettingAsync(RegistrationOpenSetting), out var open) && open;

    public Task SetRegistrationOpenAsync(bool open) => SetSettingAsync(RegistrationOpenSetting, open.ToString());

    private async Task<string?> GetSettingAsync(string key) =>
        (await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key))?.Value;

    private async Task SetSettingAsync(string key, string value)
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
