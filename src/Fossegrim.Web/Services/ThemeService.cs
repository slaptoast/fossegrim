using Microsoft.JSInterop;

namespace Fossegrim.Web.Services;

public enum ThemeChoice
{
    System,
    Light,
    Dark
}

/// <summary>
/// Reads and changes the UI theme via wwwroot/js/theme.js, which applies it to
/// the root element's data-theme attribute and remembers it in localStorage.
/// The choice is per browser, not per user.
/// </summary>
public class ThemeService
{
    private readonly IJSRuntime _jsRuntime;

    public ThemeService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task<ThemeChoice> GetChoiceAsync()
    {
        var value = await _jsRuntime.InvokeAsync<string>("fossegrimTheme.getChoice");
        return value switch
        {
            "light" => ThemeChoice.Light,
            "dark" => ThemeChoice.Dark,
            _ => ThemeChoice.System
        };
    }

    public async Task SetChoiceAsync(ThemeChoice choice)
    {
        await _jsRuntime.InvokeVoidAsync("fossegrimTheme.setChoice", choice.ToString().ToLowerInvariant());
    }
}
