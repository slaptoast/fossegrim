using TagLib;

namespace Fossegrim.Lib.Services.Library;

/// <summary>What a scan reads from one audio file.</summary>
public record AudioFileTags(
    string? Title,
    string? ArtistName,
    string? Album,
    int? Year,
    int? Track,
    string? Genre,
    TimeSpan? Duration,
    int? Bitrate,
    // Comma-separated tag fields whose ID3v1 value disagrees with ID3v2
    // (MP3 only); null when they agree or the file doesn't have both.
    string? TagVersionMismatch);

/// <summary>Reads tags with TagLib, and compares an MP3's ID3v1 tag against its ID3v2 tag.</summary>
public static class TagReader
{
    // ID3v1 text fields are fixed 30-byte slots.
    private const int Id3v1TextLength = 30;

    public static AudioFileTags Read(string path)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        var bitrate = file.Properties?.AudioBitrate;
        return new AudioFileTags(
            Normalize(tag.Title),
            Normalize(tag.FirstPerformer ?? string.Join(", ", tag.Performers ?? [])),
            Normalize(tag.Album),
            tag.Year == 0 ? null : (int)tag.Year,
            tag.Track == 0 ? null : (int)tag.Track,
            Normalize(tag.FirstGenre),
            file.Properties?.Duration is { Ticks: > 0 } duration ? duration : null,
            bitrate is > 0 ? bitrate : null,
            CompareId3Versions(file));
    }

    /// <summary>
    /// Field names where ID3v1 and ID3v2 disagree, or null. A value v1
    /// can't hold doesn't count: text longer than its 30 bytes only has to
    /// match the start, and a genre outside v1's fixed list can't be stored.
    /// </summary>
    public static string? CompareId3Versions(TagLib.File file)
    {
        if (file.GetTag(TagTypes.Id3v1) is not TagLib.Id3v1.Tag v1 || file.GetTag(TagTypes.Id3v2) is not TagLib.Id3v2.Tag v2)
        {
            return null;
        }

        var fields = new List<string>();
        if (!TextMatches(v1.Title, v2.Title)) fields.Add("title");
        if (!TextMatches(v1.FirstPerformer, v2.FirstPerformer)) fields.Add("artist");
        if (!TextMatches(v1.Album, v2.Album)) fields.Add("album");
        if (v1.Year != v2.Year) fields.Add("year");
        if (v1.Track != 0 && v1.Track != v2.Track) fields.Add("track");
        if (Normalize(v2.FirstGenre) is { } genre && TagLib.Genres.AudioToIndex(genre) != 255 &&
            !string.Equals(Normalize(v1.FirstGenre), genre, StringComparison.OrdinalIgnoreCase))
        {
            fields.Add("genre");
        }

        return fields.Count == 0 ? null : string.Join(",", fields);
    }

    /// <summary>Rewrites the ID3v1 tag from the ID3v2 tag, which is what Fossegrim reads.</summary>
    public static void SyncId3v1FromId3v2(string path)
    {
        using var file = TagLib.File.Create(path);
        if (file.GetTag(TagTypes.Id3v2) is not TagLib.Id3v2.Tag v2)
        {
            throw new InvalidOperationException("The file has no ID3v2 tag to copy from.");
        }

        var v1 = (TagLib.Id3v1.Tag)file.GetTag(TagTypes.Id3v1, create: true);
        v1.Clear();
        v2.CopyTo(v1, overwrite: true);
        file.Save();
    }

    private static bool TextMatches(string? v1, string? v2)
    {
        var a = Normalize(v1) ?? "";
        var b = Normalize(v2) ?? "";
        // v1 stores Latin-1 bytes, so compare only what fits.
        if (b.Length > Id3v1TextLength)
        {
            b = b[..Id3v1TextLength].TrimEnd();
        }
        return string.Equals(a, b, StringComparison.Ordinal) || IsLossyLatin1(a, b);
    }

    // Characters v1 can't encode come back as '?'; that's a limit of v1, not a disagreement.
    private static bool IsLossyLatin1(string v1, string v2) =>
        v1.Length == v2.Length && v1.Zip(v2).All(pair => pair.First == pair.Second || (pair.First == '?' && pair.Second > 0xFF));

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
