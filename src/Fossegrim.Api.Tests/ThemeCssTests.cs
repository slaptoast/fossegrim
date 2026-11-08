using Fossegrim.Api.Services;
using Fossegrim.Lib.Services;
using Fossegrim.Web.Services;

namespace Fossegrim.Api.Tests;

// Custom CSS in themes: what's kept, the size limits, theme files, and the
// content security policy that stops theme CSS reaching other servers.
public class ThemeCssTests
{
    private const string Css = ".player-bar { border-top: 2px solid var(--brand-violet); }";

    [Fact]
    public void Css_IsKept_AndTravelsInThemeFiles()
    {
        var theme = new CustomTheme { Name = "Lined", Css = Css };

        var imported = AppearancePreferencesFile.ParseTheme(AppearancePreferencesFile.SerializeTheme(theme));

        Assert.Equal(Css, imported!.Css);
    }

    [Theory]
    [InlineData("   \n\t ", "")]
    [InlineData("a { color: red; }\u0000\u0007", "a { color: red; }")]
    [InlineData("a {\r\n\tcolor: red;\r\n}", "a {\r\n\tcolor: red;\r\n}")]
    public void Css_IsCleanedOfControlCharacters(string css, string expected)
    {
        Assert.Equal(expected, AppearancePreferencesFile.SanitizeCss(css));
    }

    [Fact]
    public void Css_OverThePerThemeLimit_IsDropped()
    {
        var tooLong = new string('x', AppearancePreferencesFile.MaxCssLength + 1);

        Assert.Equal("", AppearancePreferencesFile.SanitizeCss(tooLong));
        Assert.Equal(AppearancePreferencesFile.MaxCssLength, AppearancePreferencesFile.SanitizeCss(tooLong[1..]).Length);
    }

    [Fact]
    public void Css_OverTheTotal_IsDroppedFromLaterThemes()
    {
        var full = "/*" + new string('x', AppearancePreferencesFile.MaxCssLength - 4) + "*/";
        var preferences = new AppearancePreferences();
        var count = AppearancePreferencesFile.MaxTotalCssLength / full.Length + 1;
        for (var i = 0; i < count; i++)
        {
            preferences.Theme.Custom.Add(new CustomTheme { Name = $"Theme {i}", Css = full });
        }

        var sanitized = AppearancePreferencesFile.Sanitize(preferences);

        Assert.True(sanitized.Theme.Custom.Sum(t => t.Css.Length) <= AppearancePreferencesFile.MaxTotalCssLength);
        Assert.Equal(full, sanitized.Theme.Custom[0].Css);
        Assert.Equal("", sanitized.Theme.Custom[^1].Css);
    }

    // Presets are held to the same rules as a user's own themes.
    [Fact]
    public void PresetThemes_PassTheSameChecksAsCustomThemes()
    {
        foreach (var preset in PresetThemes.All)
        {
            var copy = AppearancePreferencesFile.ParseTheme(AppearancePreferencesFile.SerializeTheme(preset))!;
            Assert.Equal(preset.Colors.OrderBy(c => c.Key), copy.Colors.OrderBy(c => c.Key));
            Assert.Equal(preset.Css, copy.Css);
            Assert.DoesNotContain("url(", preset.Css);
            Assert.DoesNotContain("@import", preset.Css);
        }
    }

    [Fact]
    public void PresetThemes_CanBeChosen_AndUnknownOnesFallBackToSystem()
    {
        var preset = PresetThemes.All[0];

        var chosen = AppearancePreferencesFile.Sanitize(new AppearancePreferences { Theme = { Choice = PresetThemes.ChoiceFor(preset) } });
        var unknown = AppearancePreferencesFile.Sanitize(new AppearancePreferences { Theme = { Choice = PresetThemes.ChoicePrefix + "nope" } });

        Assert.Equal(PresetThemes.ChoiceFor(preset), chosen.Theme.Choice);
        Assert.Same(preset, PresetThemes.Find(chosen.Theme.Choice));
        Assert.Equal(ThemeChoices.System, unknown.Theme.Choice);
    }

    [Fact]
    public void ImagesOnlyLoadFromTheAppAndTheCoverProviders()
    {
        var imgSrc = SecurityHeaders.ContentSecurityPolicy.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith("img-src "));
        var sources = imgSrc.Split(' ').Skip(1).ToList();

        Assert.DoesNotContain("https:", sources);
        Assert.DoesNotContain("*", sources);
        Assert.Equal(["'self'", "data:", "blob:", .. CoverImages.BrowserImageSources], sources);
        Assert.Contains("font-src 'self' data:", SecurityHeaders.ContentSecurityPolicy);
        Assert.Contains("style-src 'self' 'unsafe-inline'", SecurityHeaders.ContentSecurityPolicy);
    }

    // The browser and the server's cover downloads must agree on the hosts.
    [Fact]
    public void BrowserImageSources_MatchTheHostsCoversDownloadFrom()
    {
        foreach (var source in CoverImages.BrowserImageSources)
        {
            var example = new Uri(source.Replace("*.", "img1.") + "/cover.jpg");
            Assert.True(CoverImages.IsAllowedSource(example), $"{example} should be an allowed cover source");
        }
    }
}
