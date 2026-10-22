namespace Fossegrim.Lib.Services;

/// <summary>
/// Last.fm settings bound from the "LastFm" configuration section (or
/// LastFm__ApiKey / LastFm__ApiSecret environment variables). The key and
/// secret are empty by default; when both are set they override whatever an
/// admin saved on the Settings page (see <see cref="LastFmCredentialsHolder"/>).
/// </summary>
public class LastFmOptions
{
    public const string SectionName = "LastFm";

    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
    public string ApiUrl { get; set; } = "https://ws.audioscrobbler.com/2.0/";
    public string AuthUrl { get; set; } = "https://www.last.fm/api/auth/";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
}
