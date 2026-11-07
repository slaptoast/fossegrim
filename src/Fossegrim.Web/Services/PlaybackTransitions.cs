using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Web.Services;

/// <summary>
/// How one track leads into the next. Consecutive tracks from one album stay
/// gapless, so albums meant to flow keep flowing; anything else (random picks,
/// playlists, mixes) crossfades when that's on.
/// </summary>
public static class PlaybackTransitions
{
    public const int MinCrossfadeSeconds = 1;
    public const int MaxCrossfadeSeconds = 12;
    public const int DefaultCrossfadeSeconds = 6;

    /// <summary>
    /// Seconds the two tracks overlap: 0 is gapless, and null means the next
    /// track only loads once this one has ended, leaving an ordinary gap.
    /// </summary>
    public static int? OverlapSeconds(MediaItemDto current, MediaItemDto next, PlayerSettings settings)
    {
        if (settings.Crossfade && !IsSameAlbum(current, next))
        {
            return settings.CrossfadeSeconds;
        }

        return settings.Gapless ? 0 : null;
    }

    // By name, as that's what queue entries carry. Two albums sharing a name
    // only lose the fade between them; tracks without an album never match.
    private static bool IsSameAlbum(MediaItemDto a, MediaItemDto b) =>
        !string.IsNullOrWhiteSpace(a.Album) &&
        string.Equals(a.Album.Trim(), b.Album?.Trim(), StringComparison.OrdinalIgnoreCase);
}
