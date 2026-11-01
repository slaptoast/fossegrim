namespace Fossegrim.Lib.Services.Library;

/// <summary>
/// The audio files the library scans and streams, by extension, with the
/// content type browsers expect for each.
/// </summary>
public static class AudioFormats
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".flac"] = "audio/flac",
        [".m4a"] = "audio/mp4",
        [".ogg"] = "audio/ogg",
        [".opus"] = "audio/ogg",
    };

    public static bool IsSupported(string path) => ContentTypes.ContainsKey(Path.GetExtension(path));

    public static string? ContentType(string path) => ContentTypes.GetValueOrDefault(Path.GetExtension(path));
}
