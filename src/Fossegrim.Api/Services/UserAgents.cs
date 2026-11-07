namespace Fossegrim.Api.Services;

/// <summary>
/// Turns a browser's User-Agent into "Firefox on macOS" for the signed-in
/// devices list. Anything it doesn't recognise (e.g. a mobile app's HTTP
/// library) gives null, and the app's own ClientName is shown instead.
/// </summary>
public static class UserAgents
{
    // Order matters: Edge and Opera also say Chrome, and Chrome says Safari.
    private static readonly (string Token, string Name)[] Browsers =
    [
        ("Edg/", "Edge"),
        ("OPR/", "Opera"),
        ("Firefox/", "Firefox"),
        ("FxiOS/", "Firefox"),
        ("CriOS/", "Chrome"),
        ("Chrome/", "Chrome"),
        ("Safari/", "Safari"),
    ];

    // iPhone and iPad user agents also contain "like Mac OS X".
    private static readonly (string Token, string Name)[] Systems =
    [
        ("iPhone", "iOS"),
        ("iPad", "iPadOS"),
        ("Android", "Android"),
        ("Windows", "Windows"),
        ("Mac OS X", "macOS"),
        ("CrOS", "ChromeOS"),
        ("Linux", "Linux"),
    ];

    public static string? Describe(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        var browser = Find(Browsers, userAgent);
        var system = Find(Systems, userAgent);
        return (browser, system) switch
        {
            ({ } b, { } s) => $"{b} on {s}",
            ({ } b, null) => b,
            (null, { } s) => s,
            _ => null,
        };
    }

    private static string? Find((string Token, string Name)[] candidates, string userAgent) =>
        candidates.FirstOrDefault(c => userAgent.Contains(c.Token, StringComparison.Ordinal)).Name;
}
