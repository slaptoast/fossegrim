namespace Fossegrim.Lib.Services;

/// <summary>
/// When a play counts as a listen, both for listening history and for
/// scrobbling: the track is longer than 30 seconds and at least 90% of it
/// was heard. That's stricter than Last.fm's own rule (half, or 4 minutes),
/// so anything counted here is also accepted by Last.fm. The browser applies
/// the same rule in wwwroot/js/listenTracker.js.
/// </summary>
public static class ListenRule
{
    public const double RequiredFraction = 0.9;
    public static readonly TimeSpan MinTrackLength = TimeSpan.FromSeconds(30);

    // The browser measures the audio's actual length, while the server only
    // has the length from the file's tags, which can be a little off.
    public static readonly TimeSpan LengthTolerance = TimeSpan.FromSeconds(5);

    public static bool IsListen(TimeSpan trackLength, TimeSpan listenedFor) =>
        trackLength > MinTrackLength &&
        listenedFor >= trackLength * RequiredFraction - LengthTolerance;
}
