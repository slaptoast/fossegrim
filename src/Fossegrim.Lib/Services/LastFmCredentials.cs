namespace Fossegrim.Lib.Services;

public enum LastFmCredentialSource
{
    // LastFm:ApiKey / LastFm:ApiSecret (env vars); these override Settings.
    Configuration,
    // Entered by an admin on the Settings page and stored in the database.
    Settings
}

public record LastFmCredentials(string ApiKey, string ApiSecret, LastFmCredentialSource Source);

/// <summary>
/// The Last.fm API credentials currently in effect, or null when Last.fm is
/// off. A singleton so an admin saving new credentials takes effect
/// immediately, without a restart; whoever loads or saves them calls Set.
/// </summary>
public class LastFmCredentialsHolder
{
    private LastFmCredentials? _current;

    public LastFmCredentials? Current => Volatile.Read(ref _current);

    public void Set(LastFmCredentials? credentials) => Volatile.Write(ref _current, credentials);
}
