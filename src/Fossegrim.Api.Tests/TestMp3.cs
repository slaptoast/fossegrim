namespace Fossegrim.Api.Tests;

public record Mp3Tags(string? Title, string[] Performers, string? Album, uint Year, uint Track, string[] Genres);

/// <summary>
/// Writes small silent MP3 files that TagLib can read and tag, so tag
/// editing can be tested against real files instead of mocks.
/// </summary>
public static class TestMp3
{
    // MPEG-1 Layer III, 128 kbps, 44.1 kHz, no padding: 417-byte frames.
    private static readonly byte[] FrameHeader = [0xFF, 0xFB, 0x90, 0x64];
    private const int FrameLength = 417;

    public static string Create(string directory, string fileName, Mp3Tags tags)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        var frame = new byte[FrameLength];
        FrameHeader.CopyTo(frame, 0);
        using (var stream = File.Create(path))
        {
            for (var i = 0; i < 40; i++)
            {
                stream.Write(frame);
            }
        }

        using var file = TagLib.File.Create(path);
        file.Tag.Title = tags.Title;
        file.Tag.Performers = tags.Performers;
        file.Tag.Album = tags.Album;
        file.Tag.Year = tags.Year;
        file.Tag.Track = tags.Track;
        file.Tag.Genres = tags.Genres;
        file.Save();
        return path;
    }

    public static Mp3Tags Read(string path)
    {
        using var file = TagLib.File.Create(path);
        return new Mp3Tags(file.Tag.Title, file.Tag.Performers, file.Tag.Album, file.Tag.Year, file.Tag.Track, file.Tag.Genres);
    }
}
