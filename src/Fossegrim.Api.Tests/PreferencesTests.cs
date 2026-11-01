using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fossegrim.Api.Endpoints;
using Fossegrim.Web.Services;

namespace Fossegrim.Api.Tests;

public class PreferencesEndpointTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;

    public PreferencesEndpointTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task NothingSaved_ReturnsAnEmptyObject()
    {
        var user = await _factory.CreateUserAsync();

        Assert.Equal("{}", await user.Client.GetStringAsync("/api/me/preferences"));
    }

    [Fact]
    public async Task SavedDocument_IsReturnedAsIs_AndOnlyToItsOwner()
    {
        var user = await _factory.CreateUserAsync();
        var other = await _factory.CreateUserAsync();
        var json = """{"version":1,"theme":{"choice":"dark","custom":[]},"layouts":{},"player":{"position":"top"}}""";

        var put = await PutAsync(user, json);

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(json, await user.Client.GetStringAsync("/api/me/preferences"));
        Assert.Equal("{}", await other.Client.GetStringAsync("/api/me/preferences"));
    }

    [Fact]
    public async Task SavingAgain_ReplacesTheDocument()
    {
        var user = await _factory.CreateUserAsync();
        await PutAsync(user, """{"a":1}""");

        await PutAsync(user, """{"b":2}""");

        Assert.Equal("""{"b":2}""", await user.Client.GetStringAsync("/api/me/preferences"));
    }

    [Theory]
    [InlineData("[1,2]")]      // not an object
    [InlineData("\"dark\"")]
    [InlineData("{not json")]
    public async Task NonObjects_AreRejected(string body)
    {
        var user = await _factory.CreateUserAsync();

        var response = await PutAsync(user, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OversizedDocument_IsRejected()
    {
        var user = await _factory.CreateUserAsync();
        var big = JsonSerializer.Serialize(new { padding = new string('x', Preferences.MaxBytes) });

        var response = await PutAsync(user, big);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task RequiresSignIn()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me/preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsync("/api/me/preferences", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
    }

    private static Task<HttpResponseMessage> PutAsync(TestUser user, string json) =>
        user.Client.PutAsync("/api/me/preferences", new StringContent(json, Encoding.UTF8, "application/json"));
}

// The client-side document format: what's kept, what's dropped, and import/export.
public class AppearancePreferencesFileTests
{
    [Fact]
    public void ExportThenImport_RoundTrips()
    {
        var theme = new CustomTheme { Name = "Fjord", Base = "dark", Colors = { ["--canvas"] = "#102030", ["--brand-violet"] = "#aa5500" } };
        var original = new AppearancePreferences
        {
            Theme = new ThemeSettings { Choice = ThemeChoices.ForCustom(theme), Custom = [theme] },
            Layouts = { ["library"] = [new LayoutItem { Id = "tracks", X = 2, Y = 3, W = 10, H = 6, Hidden = true }] },
            Player = new PlayerSettings { Position = "top" },
        };

        var imported = AppearancePreferencesFile.Parse(AppearancePreferencesFile.Serialize(original))!;

        Assert.Equal(ThemeChoices.ForCustom(theme), imported.Theme.Choice);
        var importedTheme = Assert.Single(imported.Theme.Custom);
        Assert.Equal(("Fjord", "dark"), (importedTheme.Name, importedTheme.Base));
        Assert.Equal(theme.Colors, importedTheme.Colors);
        var item = Assert.Single(imported.Layouts["library"]);
        Assert.Equal((2, 3, 10, 6, true), (item.X, item.Y, item.W, item.H, item.Hidden));
        Assert.Equal("top", imported.Player.Position);
    }

    [Fact]
    public void VisualizerSettings_RoundTrip_AndDefaultWhenMissing()
    {
        var original = new AppearancePreferences { Visualizer = new VisualizerSettings { FlashingNoticeAccepted = true, AutoCycle = false } };

        var imported = AppearancePreferencesFile.Parse(AppearancePreferencesFile.Serialize(original))!;
        var older = AppearancePreferencesFile.Parse("""{"theme":{"choice":"dark","custom":[]}}""")!;

        Assert.Equal((true, false), (imported.Visualizer.FlashingNoticeAccepted, imported.Visualizer.AutoCycle));
        // Documents saved before the visualizer existed: the notice is shown and cycling is on.
        Assert.Equal((false, true), (older.Visualizer.FlashingNoticeAccepted, older.Visualizer.AutoCycle));
    }

    [Fact]
    public void AutoFill_RoundTrips_AndIsOffByDefault()
    {
        var original = new AppearancePreferences { Player = new PlayerSettings { AutoFill = true } };

        var imported = AppearancePreferencesFile.Parse(AppearancePreferencesFile.Serialize(original))!;
        var older = AppearancePreferencesFile.Parse("""{"player":{"position":"top"}}""")!;

        Assert.True(imported.Player.AutoFill);
        Assert.False(older.Player.AutoFill);
    }

    [Fact]
    public void RandomSettings_RoundTrip_AndFallBackToThePreferredMode()
    {
        var original = new AppearancePreferences { Random = new RandomSettings { PreferredMode = RandomModes.Full, AlbumMode = RandomModes.Adjusted } };

        var imported = AppearancePreferencesFile.Parse(AppearancePreferencesFile.Serialize(original))!;
        var unknown = AppearancePreferencesFile.Parse("""{"random":{"preferredMode":"shuffle","albumMode":"shuffle","trackMode":null}}""")!;
        var older = AppearancePreferencesFile.Parse("""{"theme":{"choice":"dark","custom":[]}}""")!;

        Assert.Equal((RandomModes.Full, RandomModes.Adjusted, null), (imported.Random.PreferredMode, imported.Random.AlbumMode, imported.Random.TrackMode));
        // No override: a module follows the preferred mode.
        Assert.Equal((RandomModes.Adjusted, RandomModes.Full), (imported.Random.AlbumModeOrPreferred, imported.Random.TrackModeOrPreferred));
        Assert.Equal((RandomModes.Adjusted, null, null), (unknown.Random.PreferredMode, unknown.Random.AlbumMode, unknown.Random.TrackMode));
        Assert.Equal((RandomModes.Adjusted, null, null), (older.Random.PreferredMode, older.Random.AlbumMode, older.Random.TrackMode));
    }

    [Theory]
    [InlineData("url(https://example.com/track.png)")] // would make the browser fetch
    [InlineData("red; background: url(x)")]
    [InlineData("var(--canvas)")]
    [InlineData("#12345")]
    [InlineData("red")]
    public void UnsafeOrNonHexColors_AreDropped(string value)
    {
        var json = Document(colors: $$"""{"--canvas": {{JsonSerializer.Serialize(value)}}, "--surface": "#ABCDEF"}""");

        var theme = Assert.Single(AppearancePreferencesFile.Parse(json)!.Theme.Custom);

        Assert.Equal(new Dictionary<string, string> { ["--surface"] = "#abcdef" }, theme.Colors);
    }

    [Fact]
    public void UnknownTokens_AreDropped()
    {
        var json = Document(colors: """{"--player-error": "#ff0000", "background": "#ff0000", "--ink": "#000000"}""");

        var theme = Assert.Single(AppearancePreferencesFile.Parse(json)!.Theme.Custom);

        Assert.Equal(["--ink"], theme.Colors.Keys);
    }

    [Fact]
    public void ChoiceOfAMissingCustomTheme_FallsBackToSystem()
    {
        var json = """{"theme":{"choice":"custom:gone","custom":[]}}""";

        Assert.Equal(ThemeChoices.System, AppearancePreferencesFile.Parse(json)!.Theme.Choice);
    }

    [Fact]
    public void LayoutValues_AreClampedToTheGrid_AndBadIdsDropped()
    {
        var json = """
            {"layouts":{
                "library":[{"id":"tracks","x":-5,"y":-1,"w":99,"h":0},{"id":"bad id!","x":0,"y":0,"w":4,"h":4}],
                "../etc":[{"id":"x","x":0,"y":0,"w":4,"h":4}]
            }}
            """;

        var layouts = AppearancePreferencesFile.Parse(json)!.Layouts;

        var item = Assert.Single(Assert.Single(layouts).Value);
        Assert.Equal(("tracks", 0, 0, 24, 1), (item.Id, item.X, item.Y, item.W, item.H));
    }

    [Fact]
    public void TooManyCustomThemes_AreCappedAndLongNamesTrimmed()
    {
        var themes = Enumerable.Range(0, 60)
            .Select(i => new { id = $"t{i}", name = new string('n', 100), @base = "light", colors = new Dictionary<string, string>() });
        var json = JsonSerializer.Serialize(new { theme = new { choice = "light", custom = themes } });

        var parsed = AppearancePreferencesFile.Parse(json)!;

        Assert.Equal(AppearancePreferencesFile.MaxCustomThemes, parsed.Theme.Custom.Count);
        Assert.All(parsed.Theme.Custom, t => Assert.Equal(AppearancePreferencesFile.MaxNameLength, t.Name.Length));
    }

    [Fact]
    public void SingleTheme_ExportsAndImportsWithANewId()
    {
        var theme = new CustomTheme { Name = "Shared", Base = "light", Colors = { ["--canvas"] = "#eeeeee" } };

        var imported = AppearancePreferencesFile.ParseTheme(AppearancePreferencesFile.SerializeTheme(theme))!;

        Assert.NotEqual(theme.Id, imported.Id);
        Assert.Equal(("Shared", "light"), (imported.Name, imported.Base));
        Assert.Equal(theme.Colors, imported.Colors);
    }

    [Fact]
    public void ThemeFilesAndSettingsFiles_AreNotMixedUp()
    {
        var themeFile = AppearancePreferencesFile.SerializeTheme(new CustomTheme());
        var settingsFile = AppearancePreferencesFile.Serialize(new AppearancePreferences());

        Assert.Null(AppearancePreferencesFile.Parse(themeFile));
        Assert.Null(AppearancePreferencesFile.ParseTheme(settingsFile));
        Assert.Null(AppearancePreferencesFile.Parse("not json"));
        Assert.Null(AppearancePreferencesFile.ParseTheme("[]"));
    }

    [Fact]
    public void BuiltInTokenDefaults_MatchTheStylesheet()
    {
        // ThemeTokens repeats the :root and dark values from app.css for the editor; keep them in step.
        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Fossegrim.Web", "wwwroot", "css", "app.css"));
        var light = Tokens(css, ":root {");
        var dark = new Dictionary<string, string>(light);
        foreach (var (name, value) in Tokens(css, ":root[data-theme=\"dark\"] {"))
        {
            dark[name] = value;
        }

        Assert.All(ThemeTokens.All, token =>
        {
            Assert.Equal(light[token.Name], token.Light);
            Assert.Equal(dark[token.Name], token.Dark);
        });
    }

    private static string Document(string colors) =>
        """{"theme":{"choice":"custom:abc","custom":[{"id":"abc","name":"T","base":"dark","colors":""" + colors + "}]}}";

    private static Dictionary<string, string> Tokens(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        var block = css[start..css.IndexOf('}', start)];
        return System.Text.RegularExpressions.Regex.Matches(block, @"(--[\w-]+):\s*([^;]+);")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Fossegrim.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Couldn't find the repository root.");
    }
}
