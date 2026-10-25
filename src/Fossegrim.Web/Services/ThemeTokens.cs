namespace Fossegrim.Web.Services;

public record ThemeToken(string Name, string Label, string Group, string Light, string Dark)
{
    public string DefaultFor(string baseTheme) => baseTheme == ThemeChoices.Dark ? Dark : Light;
}

/// <summary>
/// The colors a custom theme can change, with their built-in values (which
/// must match :root and :root[data-theme="dark"] in app.css). Shades that
/// follow from these, like hover colors and the brand gradient, are derived
/// in js/theme.js rather than edited separately.
/// </summary>
public static class ThemeTokens
{
    public static readonly IReadOnlyList<ThemeToken> All =
    [
        new("--brand-violet", "Buttons", "Accent", "#764ba2", "#764ba2"),
        new("--brand-indigo", "Focus and highlights", "Accent", "#667eea", "#8b9cf4"),
        new("--accent-fg", "Links and artist names", "Accent", "#764ba2", "#c4a7ec"),
        new("--canvas", "Page background", "Page", "#eceef2", "#121418"),
        new("--surface", "Panels", "Page", "#ffffff", "#1b1e23"),
        new("--surface-muted", "Panel headers and hover", "Page", "#f8f9fa", "#23272d"),
        new("--ink", "Text", "Text", "#212529", "#e9ecef"),
        new("--text-secondary", "Secondary text", "Text", "#495057", "#c3c9d0"),
        new("--text-muted", "Muted text", "Text", "#6c757d", "#9ea6af"),
        new("--border", "Dividers", "Lines", "#e9ecef", "#2f343b"),
        new("--border-strong", "Borders", "Lines", "#dee2e6", "#6c7480"),
        new("--chrome-bg", "Background", "Top and player bars", "#212529", "#0d0f12"),
        new("--chrome-fg", "Text", "Top and player bars", "#ffffff", "#ffffff"),
        new("--chrome-muted", "Secondary text", "Top and player bars", "#adb5bd", "#adb5bd"),
        new("--selection-bg", "Background", "Selection", "#eef2ff", "#2b3058"),
        new("--selection-fg", "Text", "Selection", "#3949ab", "#c9d0ff"),
        new("--danger", "Delete buttons", "Danger", "#dc3545", "#dc3545"),
    ];

    private static readonly HashSet<string> Names = All.Select(t => t.Name).ToHashSet();

    public static bool IsEditable(string name) => Names.Contains(name);
}
