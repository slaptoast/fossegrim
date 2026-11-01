using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Fossegrim.Web.Services;

/// <summary>
/// A user's appearance settings: theme, custom themes, page layouts, the
/// player bar position and the visualizer. Saved to the server as one JSON document (see
/// /api/me/preferences) and used as-is for export/import, so the same file
/// works on any Fossegrim server.
/// </summary>
public sealed class AppearancePreferences
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public ThemeSettings Theme { get; set; } = new();

    // Page name (e.g. "library") -> its module layout. A missing page uses its default.
    public Dictionary<string, List<LayoutItem>> Layouts { get; set; } = [];

    public PlayerSettings Player { get; set; } = new();
    public VisualizerSettings Visualizer { get; set; } = new();
    public RandomSettings Random { get; set; } = new();
}

public sealed class ThemeSettings
{
    // "system", "light", "dark", or "custom:<id>" for one of Custom.
    public string Choice { get; set; } = ThemeChoices.System;
    public List<CustomTheme> Custom { get; set; } = [];
}

public sealed class CustomTheme
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "My theme";

    // "light" or "dark": the built-in theme it starts from. Any color not in
    // Colors comes from the base, and so do the native controls (color-scheme).
    public string Base { get; set; } = ThemeChoices.Light;

    // Token name (see ThemeTokens) -> "#rrggbb".
    public Dictionary<string, string> Colors { get; set; } = [];

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

// A module's place on a page grid, in GridStack units (24 columns; rows are a fixed height).
public sealed class LayoutItem
{
    public string Id { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public bool Hidden { get; set; }
}

public sealed class PlayerSettings
{
    // "bottom" or "top"
    public string Position { get; set; } = "bottom";

    // When the queue runs out, keep going with random picks (see PlayerState).
    public bool AutoFill { get; set; }
}

public sealed class VisualizerSettings
{
    // The viewer has seen the flashing-lights notice and chosen to start it.
    public bool FlashingNoticeAccepted { get; set; }

    // A new preset every 20 seconds.
    public bool AutoCycle { get; set; } = true;
}

// Which kind of random the user prefers (set on the Profile page), and the
// front-page Random modules' own toggles, which override it when set.
public sealed class RandomSettings
{
    public string PreferredMode { get; set; } = RandomModes.Adjusted;
    public string? AlbumMode { get; set; }
    public string? TrackMode { get; set; }

    [JsonIgnore]
    public string AlbumModeOrPreferred => AlbumMode ?? PreferredMode;
    [JsonIgnore]
    public string TrackModeOrPreferred => TrackMode ?? PreferredMode;
}

// Full ignores listening history; Adjusted favours what hasn't been played recently.
public static class RandomModes
{
    public const string Full = "full";
    public const string Adjusted = "adjusted";

    public static string Sanitize(string? mode) => mode == Full ? Full : Adjusted;

    // A module override: null (follow the preferred mode) unless it's a known mode.
    public static string? SanitizeOverride(string? mode) => mode is Full or Adjusted ? mode : null;
}

public static class ThemeChoices
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";
    public const string CustomPrefix = "custom:";

    public static string ForCustom(CustomTheme theme) => CustomPrefix + theme.Id;
}

/// <summary>
/// Reading, writing and cleaning up preference documents. Everything that
/// comes from outside (the server, an imported file) goes through
/// <see cref="Sanitize"/>, which keeps only known settings and safe values:
/// colors in particular end up in CSS custom properties, which accept any
/// text, so an imported "color" like url(...) could otherwise make the
/// browser fetch from elsewhere.
/// </summary>
public static partial class AppearancePreferencesFile
{
    public const int MaxCustomThemes = 50;
    public const int MaxNameLength = 60;
    public const int GridColumns = 24;
    private const int MaxRows = 200;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex SafeId();

    public static string Serialize(AppearancePreferences preferences) => JsonSerializer.Serialize(preferences, Options);

    public static string SerializeTheme(CustomTheme theme) => JsonSerializer.Serialize(new ThemeFile(theme), Options);

    /// <summary>Parses and sanitizes a document; null if it isn't one.</summary>
    public static AppearancePreferences? Parse(string json)
    {
        try
        {
            if (IsThemeFile(json))
            {
                return null;
            }

            var parsed = JsonSerializer.Deserialize<AppearancePreferences>(json, Options);
            return parsed is null ? null : Sanitize(parsed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses an exported single theme. It gets a new id so importing it
    /// twice, or importing a friend's copy of your own theme, doesn't clash.
    /// </summary>
    public static CustomTheme? ParseTheme(string json)
    {
        try
        {
            if (!IsThemeFile(json))
            {
                return null;
            }

            var file = JsonSerializer.Deserialize<ThemeFile>(json, Options);
            if (file?.Theme is not { } theme)
            {
                return null;
            }

            theme.Id = CustomTheme.NewId();
            return SanitizeTheme(theme);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsThemeFile(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("kind", out var kind) &&
            kind.ValueKind == JsonValueKind.String &&
            kind.GetString() == ThemeFileKind;
    }

    private const string ThemeFileKind = "fossegrim-theme";

    public static AppearancePreferences Sanitize(AppearancePreferences preferences)
    {
        var themes = (preferences.Theme?.Custom ?? [])
            .Where(t => t is not null)
            .Select(SanitizeTheme)
            .Where(t => t is not null)
            .Select(t => t!)
            .DistinctBy(t => t.Id)
            .Take(MaxCustomThemes)
            .ToList();

        var choice = preferences.Theme?.Choice ?? ThemeChoices.System;
        var choiceIsValid = choice is ThemeChoices.System or ThemeChoices.Light or ThemeChoices.Dark ||
            (choice.StartsWith(ThemeChoices.CustomPrefix, StringComparison.Ordinal) &&
             themes.Any(t => ThemeChoices.ForCustom(t) == choice));

        var layouts = new Dictionary<string, List<LayoutItem>>();
        foreach (var (page, items) in preferences.Layouts ?? [])
        {
            if (!SafeId().IsMatch(page ?? "") || items is null)
            {
                continue;
            }

            layouts[page!] = items
                .Where(i => i is not null && SafeId().IsMatch(i.Id ?? ""))
                .DistinctBy(i => i.Id)
                .Select(i => new LayoutItem
                {
                    Id = i.Id,
                    W = Math.Clamp(i.W, 1, GridColumns),
                    X = Math.Clamp(i.X, 0, GridColumns - Math.Clamp(i.W, 1, GridColumns)),
                    H = Math.Clamp(i.H, 1, MaxRows),
                    Y = Math.Clamp(i.Y, 0, MaxRows),
                    Hidden = i.Hidden,
                })
                .ToList();
        }

        return new AppearancePreferences
        {
            Version = AppearancePreferences.CurrentVersion,
            Theme = new ThemeSettings
            {
                Choice = choiceIsValid ? choice : ThemeChoices.System,
                Custom = themes,
            },
            Layouts = layouts,
            Player = new PlayerSettings
            {
                Position = preferences.Player?.Position == "top" ? "top" : "bottom",
                AutoFill = preferences.Player?.AutoFill ?? false,
            },
            Visualizer = new VisualizerSettings
            {
                FlashingNoticeAccepted = preferences.Visualizer?.FlashingNoticeAccepted ?? false,
                AutoCycle = preferences.Visualizer?.AutoCycle ?? true,
            },
            Random = new RandomSettings
            {
                PreferredMode = RandomModes.Sanitize(preferences.Random?.PreferredMode),
                AlbumMode = RandomModes.SanitizeOverride(preferences.Random?.AlbumMode),
                TrackMode = RandomModes.SanitizeOverride(preferences.Random?.TrackMode),
            },
        };
    }

    private static CustomTheme? SanitizeTheme(CustomTheme theme)
    {
        if (!SafeId().IsMatch(theme.Id ?? ""))
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(theme.Name) ? "Untitled theme" : theme.Name.Trim();
        return new CustomTheme
        {
            Id = theme.Id,
            Name = name.Length > MaxNameLength ? name[..MaxNameLength] : name,
            Base = theme.Base == ThemeChoices.Dark ? ThemeChoices.Dark : ThemeChoices.Light,
            Colors = (theme.Colors ?? [])
                .Where(c => ThemeTokens.IsEditable(c.Key) && c.Value is not null && HexColor().IsMatch(c.Value))
                .ToDictionary(c => c.Key, c => c.Value.ToLowerInvariant()),
        };
    }

    // An exported single theme: marked so importing a full settings file
    // into the theme importer (or vice versa) is caught.
    private sealed class ThemeFile
    {
        public ThemeFile()
        {
        }

        public ThemeFile(CustomTheme theme)
        {
            Theme = theme;
        }

        public string Kind { get; set; } = ThemeFileKind;
        public CustomTheme? Theme { get; set; }
    }
}
