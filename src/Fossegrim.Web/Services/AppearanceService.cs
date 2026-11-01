using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Fossegrim.Web.Services;

/// <summary>
/// The signed-in user's appearance settings (theme, custom themes, layouts,
/// player position). Loaded from their profile on the server, applied to the
/// page through js/theme.js, and saved back shortly after each change, so
/// they're the same in every browser.
///
/// Nothing is saved until the profile has loaded: if loading fails, editing
/// stays local rather than overwriting the saved settings with defaults.
/// </summary>
public class AppearanceService : IPlaybackSettings
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly FossegrimApiClient _apiClient;
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly IJSRuntime _js;
    private readonly ToastService _toasts;
    private string? _loadedForUserId;
    private Task? _loading;
    private CancellationTokenSource? _pendingSave;

    public AppearanceService(FossegrimApiClient apiClient, AuthenticationStateProvider authStateProvider, IJSRuntime js, ToastService toasts)
    {
        _apiClient = apiClient;
        _authStateProvider = authStateProvider;
        _js = js;
        _toasts = toasts;
    }

    public AppearancePreferences Current { get; private set; } = new();

    // False until the profile has loaded (or when it failed to).
    public bool IsLoaded => _loadedForUserId is not null;

    public bool AutoFillQueue => Current.Player.AutoFill;

    public string PreferredRandomMode => Current.Random.PreferredMode;

    public event Action? Changed;

    /// <summary>
    /// Loads the signed-in user's settings, once per user. Safe to call from
    /// every page; later calls for the same user return straight away.
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        var userId = await GetUserIdAsync();
        if (userId is null || userId == _loadedForUserId)
        {
            return;
        }

        _loading ??= LoadAsync(userId);
        try
        {
            await _loading;
        }
        finally
        {
            _loading = null;
        }
    }

    public CustomTheme? ActiveCustomTheme => Current.Theme.Choice.StartsWith(ThemeChoices.CustomPrefix, StringComparison.Ordinal)
        ? Current.Theme.Custom.FirstOrDefault(t => ThemeChoices.ForCustom(t) == Current.Theme.Choice)
        : null;

    /// <summary>
    /// Changes the settings: <paramref name="change"/> edits a copy, which is
    /// cleaned up, applied straight away, and saved shortly after.
    /// </summary>
    public async Task UpdateAsync(Action<AppearancePreferences> change)
    {
        var copy = AppearancePreferencesFile.Parse(AppearancePreferencesFile.Serialize(Current)) ?? new AppearancePreferences();
        change(copy);
        Current = AppearancePreferencesFile.Sanitize(copy);
        await ApplyThemeAsync();
        Changed?.Invoke();
        ScheduleSave();
    }

    /// <summary>Replaces every setting (an import), saving straight away.</summary>
    public async Task ReplaceAsync(AppearancePreferences preferences)
    {
        Current = AppearancePreferencesFile.Sanitize(preferences);
        await ApplyThemeAsync();
        Changed?.Invoke();
        _pendingSave?.Cancel();
        await SaveAsync();
    }

    public Task SetThemeChoiceAsync(string choice) => UpdateAsync(p => p.Theme.Choice = choice);

    public string Export() => AppearancePreferencesFile.Serialize(Current);

    private async Task LoadAsync(string userId)
    {
        try
        {
            var json = await _apiClient.GetPreferencesJsonAsync();
            Current = AppearancePreferencesFile.Parse(json) ?? new AppearancePreferences();
            _loadedForUserId = userId;
        }
        catch (Exception ex)
        {
            _toasts.Show($"Couldn't load your appearance settings: {ex.Message}");
            return;
        }

        await ApplyThemeAsync();
        Changed?.Invoke();
    }

    private async Task ApplyThemeAsync()
    {
        var custom = ActiveCustomTheme;
        var choice = Current.Theme.Choice;
        var state = custom is not null
            ? new { choice, @base = custom.Base, colors = custom.Colors }
            : new { choice, @base = choice is ThemeChoices.Light or ThemeChoices.Dark ? choice : null as string, colors = new Dictionary<string, string>() };
        await _js.InvokeVoidAsync("fossegrimTheme.apply", state);
    }

    private void ScheduleSave()
    {
        _pendingSave?.Cancel();
        var pending = _pendingSave = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SaveDelay, pending.Token);
            }
            catch (TaskCanceledException)
            {
                return; // a later change will save
            }

            await SaveAsync();
        });
    }

    private async Task SaveAsync()
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            await _apiClient.SavePreferencesJsonAsync(AppearancePreferencesFile.Serialize(Current));
        }
        catch (Exception ex)
        {
            _toasts.Show($"Couldn't save your appearance settings: {ex.Message}");
        }
    }

    private async Task<string?> GetUserIdAsync()
    {
        var state = await _authStateProvider.GetAuthenticationStateAsync();
        return state.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    }
}
