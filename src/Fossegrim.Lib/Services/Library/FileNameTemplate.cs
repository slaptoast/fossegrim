using System.Text;
using System.Text.RegularExpressions;

namespace Fossegrim.Lib.Services.Library;

/// <summary>The tag values a file-name template is filled from.</summary>
public record FileNameTags(string? Artist, string? Album, string? Title, int? Track, int? Year, string? Genre);

/// <summary>
/// Turns tags into a path relative to a media folder, e.g.
/// "{artist}/{album}/{track:00} - {title}" gives
/// "Massive Attack/Mezzanine/03 - Teardrop.mp3". "/" separates folders; the
/// original extension is always kept. Each folder or file name is made safe
/// for every OS (no reserved characters, no trailing dots or spaces), and a
/// name left empty by missing tags falls back to "Unknown ...".
/// </summary>
public static partial class FileNameTemplate
{
    public const string Default = "{artist}/{album}/{track:00} - {title}";

    private static readonly string[] Tokens = ["artist", "album", "title", "track", "year", "genre"];

    // Longest folder or file name most file systems accept, leaving room for the extension.
    private const int MaxNameLength = 200;

    /// <summary>Null when the template is usable, otherwise why not.</summary>
    public static string? Validate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "The template can't be empty.";
        }

        foreach (Match match in TokenPattern().Matches(template))
        {
            var name = match.Groups[1].Value;
            var format = match.Groups[2];
            if (!Tokens.Contains(name))
            {
                return $"Unknown token {{{name}}}. Use {string.Join(", ", Tokens.Select(t => $"{{{t}}}"))}.";
            }
            if (format.Success && name != "track" && name != "year")
            {
                return $"Only {{track}} and {{year}} take a number format, like {{track:00}}.";
            }
        }

        if (!template.Contains("{title}"))
        {
            // Without the title, every track on an album gets the same name.
            return "The template must include {title}.";
        }

        if (template.StartsWith('/') || template.Split('/').Any(segment => segment.Trim() is "" or "." or ".."))
        {
            return "The template must be a relative path without empty, \".\" or \"..\" folders.";
        }

        return null;
    }

    /// <summary>The relative path (with '/' separators) for a file with these tags and extension.</summary>
    public static string Render(string template, FileNameTags tags, string extension)
    {
        var segments = template.Split('/');
        var rendered = segments.Select((segment, index) =>
        {
            var isFileName = index == segments.Length - 1;
            var name = TokenPattern().Replace(segment, match => Value(match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null, tags));
            name = Sanitize(name);
            return name.Length > 0 ? name : Fallback(segment, isFileName);
        });

        return string.Join('/', rendered) + extension.ToLowerInvariant();
    }

    private static string Value(string token, string? format, FileNameTags tags) => token switch
    {
        "artist" => tags.Artist ?? "",
        "album" => tags.Album ?? "",
        "title" => tags.Title ?? "",
        "genre" => tags.Genre ?? "",
        "track" => tags.Track is { } track ? track.ToString(format ?? "0") : "",
        "year" => tags.Year is { } year ? year.ToString(format ?? "0") : "",
        _ => ""
    };

    // A name made only of tokens with no value falls back on its first token.
    private static string Fallback(string segment, bool isFileName)
    {
        var token = TokenPattern().Match(segment) is { Success: true } match ? match.Groups[1].Value : null;
        return token switch
        {
            "artist" => "Unknown Artist",
            "album" => "Unknown Album",
            "genre" => "Unknown Genre",
            "year" => "Unknown Year",
            _ => isFileName ? "Untitled" : "Unknown"
        };
    }

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            // Reserved on Windows or macOS, or a control character: never safe in a name.
            builder.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '_' : c);
        }

        var result = WhitespaceRun().Replace(builder.ToString(), " ");
        // A token with no value leaves dangling separators, e.g. " - Title" with no track.
        result = result.Trim().Trim('-', '_', '.', ' ').Trim();
        if (result.Length > MaxNameLength)
        {
            result = result[..MaxNameLength].TrimEnd(' ', '.');
        }
        return result;
    }

    [GeneratedRegex(@"\{(\w+)(?::([^}]+))?\}")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
