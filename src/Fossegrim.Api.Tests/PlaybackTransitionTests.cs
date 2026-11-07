using Fossegrim.Contracts.Dtos;
using Fossegrim.Web.Services;

namespace Fossegrim.Api.Tests;

public class PlaybackTransitionTests
{
    private static MediaItemDto Track(string? album) =>
        new(Guid.NewGuid(), "T", "Artist", album, null, null, null, null, null, null, null, [], []);

    private static PlayerSettings Settings(bool gapless, bool crossfade, int seconds = 6) =>
        new() { Gapless = gapless, Crossfade = crossfade, CrossfadeSeconds = seconds };

    [Theory]
    // Different albums crossfade when that's on; otherwise gapless, otherwise a gap.
    [InlineData("One", "Two", true, true, 8)]
    [InlineData("One", "Two", false, true, 8)]
    [InlineData("One", "Two", true, false, 0)]
    [InlineData("One", "Two", false, false, null)]
    // The same album never fades: it follows the gapless setting.
    [InlineData("One", "One", true, true, 0)]
    [InlineData("One", " one ", true, true, 0)]
    [InlineData("One", "One", false, true, null)]
    // No album name means no known album, so nothing matches.
    [InlineData(null, null, true, true, 8)]
    [InlineData("", "", true, true, 8)]
    [InlineData("One", null, true, true, 8)]
    public void Overlap_FollowsTheAlbumAndTheSettings(string? current, string? next, bool gapless, bool crossfade, int? expected)
    {
        var overlap = PlaybackTransitions.OverlapSeconds(Track(current), Track(next), Settings(gapless, crossfade, seconds: 8));

        Assert.Equal(expected, overlap);
    }

    [Fact]
    public void Overlap_UsesAlbumIdsWhenBothTracksHaveThem()
    {
        var settings = Settings(gapless: true, crossfade: true, seconds: 8);
        var (one, other) = (new AlbumSummaryDto(Guid.NewGuid(), "Greatest Hits", null), new AlbumSummaryDto(Guid.NewGuid(), "Greatest Hits", null));
        MediaItemDto On(AlbumSummaryDto album) => Track(album.Name) with { Albums = [album] };

        Assert.Equal(0, PlaybackTransitions.OverlapSeconds(On(one), On(one), settings));
        // Same name, different albums: they crossfade.
        Assert.Equal(8, PlaybackTransitions.OverlapSeconds(On(one), On(other), settings));
        // Only one side knows its album: fall back to the name.
        Assert.Equal(0, PlaybackTransitions.OverlapSeconds(On(one), Track("Greatest Hits"), settings));
    }

    [Fact]
    public void QueueEntries_KeepTheirAlbumId()
    {
        var albumId = Guid.NewGuid();

        var withAlbum = PlaylistTrackMapper.ToMediaItemDto(new PlaylistTrackDto(Guid.NewGuid(), "T", "A", "One", null, albumId));
        var without = PlaylistTrackMapper.ToMediaItemDto(new PlaylistTrackDto(Guid.NewGuid(), "T", "A", "One", null));

        Assert.Equal([albumId], withAlbum.Albums.Select(a => a.Id));
        Assert.Empty(without.Albums);
    }

    [Fact]
    public void Settings_RoundTrip_WithGaplessOnAndCrossfadeOffForOlderDocuments()
    {
        var original = new AppearancePreferences { Player = Settings(gapless: false, crossfade: true, seconds: 9) };

        var imported = AppearancePreferencesFile.Parse(AppearancePreferencesFile.Serialize(original))!;
        var older = AppearancePreferencesFile.Parse("""{"player":{"position":"top"}}""")!;
        var noPlayer = AppearancePreferencesFile.Parse("""{"player":null}""")!;

        Assert.Equal((false, true, 9), (imported.Player.Gapless, imported.Player.Crossfade, imported.Player.CrossfadeSeconds));
        Assert.Equal((true, false, 6), (older.Player.Gapless, older.Player.Crossfade, older.Player.CrossfadeSeconds));
        Assert.Equal((true, false, 6), (noPlayer.Player.Gapless, noPlayer.Player.Crossfade, noPlayer.Player.CrossfadeSeconds));
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(12, 12)]
    [InlineData(600, 12)]
    public void CrossfadeSeconds_IsKeptInRange(int stored, int expected)
    {
        var parsed = AppearancePreferencesFile.Parse("{\"player\":{\"crossfadeSeconds\":" + stored + "}}")!;

        Assert.Equal(expected, parsed.Player.CrossfadeSeconds);
    }
}
