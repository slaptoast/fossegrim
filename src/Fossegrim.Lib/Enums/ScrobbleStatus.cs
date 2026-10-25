namespace Fossegrim.Lib.Enums;

/// <summary>
/// Where a play is in being sent to Last.fm. Plays recorded while the user
/// hadn't linked Last.fm (or that didn't meet the scrobble rule) have no
/// status at all and are never sent.
/// </summary>
public enum ScrobbleStatus
{
    // Waiting to be sent, or to be retried after a temporary failure.
    Pending,
    // Accepted by Last.fm.
    Scrobbled,
    // Received but not counted by Last.fm (e.g. its artist/track filters).
    Ignored,
    // Given up on: a permanent error, an unlinked account, or too old to send.
    Failed
}
