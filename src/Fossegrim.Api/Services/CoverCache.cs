using Fossegrim.Lib.Services;
using SkiaSharp;

namespace Fossegrim.Api.Services;

/// <summary>
/// Album covers as /cover serves them: full size or shrunk to one of
/// <see cref="Sizes"/>, cached on disk under Covers:CacheDirectory. Cached
/// files are named after the cover's version, so a changed cover is
/// re-extracted and the stale file replaced.
/// </summary>
public class CoverCache
{
    /// <summary>The longest side a resized cover can have; a requested size rounds up to the next one.</summary>
    public static readonly int[] Sizes = [64, 128, 256, 512, 1024];

    private const int JpegQuality = 85;

    private readonly MediaLibraryService _library;
    private readonly ILogger<CoverCache> _logger;
    private readonly string _directory;

    public CoverCache(MediaLibraryService library, IConfiguration configuration, ILogger<CoverCache> logger)
    {
        _library = library;
        _logger = logger;
        var configured = configuration["Covers:CacheDirectory"];
        _directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "fossegrim", "covers")
            : configured;
    }

    /// <summary>The size a request for <paramref name="size"/> gets: one of <see cref="Sizes"/>, or null for full size.</summary>
    public static int? SizeFor(int? size) =>
        size is { } requested ? Sizes.Where(s => s >= requested).Cast<int?>().FirstOrDefault() : null;

    /// <summary>The cover's current version (its ETag), or null when the album has no cover sources.</summary>
    public Task<string?> GetVersionAsync(Guid albumId) => _library.GetAlbumCoverVersionAsync(albumId);

    /// <summary>The cached cover file and its content type, or null when the album has no cover.</summary>
    public async Task<(string Path, string ContentType)?> GetAsync(Guid albumId, int? size, string version, CancellationToken cancellationToken)
    {
        var prefix = $"{albumId:N}-{(size is { } s ? s.ToString() : "full")}-";
        var name = prefix + version;

        Directory.CreateDirectory(_directory);
        if (Directory.EnumerateFiles(_directory, name + ".*").FirstOrDefault() is { } cached)
        {
            return (cached, ContentTypeOf(cached));
        }

        if (await _library.GetAlbumCoverAsync(albumId) is not { } cover)
        {
            return null;
        }

        var (data, contentType) = size is { } longest ? Resize(cover.Data, cover.ContentType, longest) : cover;
        var path = Path.Combine(_directory, name + ExtensionOf(contentType));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temporary, data, cancellationToken);
        File.Move(temporary, path, overwrite: true);

        // Earlier versions of this cover at this size.
        foreach (var stale in Directory.EnumerateFiles(_directory, prefix + "*").Where(f => !f.EndsWith(".tmp") && Path.GetFileNameWithoutExtension(f) != name))
        {
            TryDelete(stale);
        }

        return (path, contentType);
    }

    // Shrinks the image so its longest side is at most `longest`, keeping its
    // format. Images already that small, or that can't be decoded, are kept.
    private (byte[] Data, string ContentType) Resize(byte[] data, string contentType, int longest)
    {
        // SKBitmap.Decode throws on data it can't read; SKCodec.Create returns null.
        using var encodedData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(encodedData);
        using var original = codec is null ? null : SKBitmap.Decode(codec);
        if (original is null)
        {
            _logger.LogWarning("Couldn't decode a {ContentType} cover to resize it; serving it full size.", contentType);
            return (data, contentType);
        }
        if (Math.Max(original.Width, original.Height) <= longest)
        {
            return (data, contentType);
        }

        var scale = (double)longest / Math.Max(original.Width, original.Height);
        var info = new SKImageInfo(
            Math.Max(1, (int)Math.Round(original.Width * scale)),
            Math.Max(1, (int)Math.Round(original.Height * scale)));
        using var resized = original.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var image = SKImage.FromBitmap(resized);

        var png = contentType == "image/png";
        using var encoded = image.Encode(png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, JpegQuality);
        return (encoded.ToArray(), png ? "image/png" : "image/jpeg");
    }

    // Embedded art is usually JPEG or PNG, but tags can hold other images.
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
    };

    private static string ExtensionOf(string contentType) => Extensions.GetValueOrDefault(contentType, ".jpg");

    private static string ContentTypeOf(string path) =>
        Extensions.FirstOrDefault(e => e.Value.Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)).Key ?? "image/jpeg";

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Couldn't delete the old cached cover {Path}.", path);
        }
    }
}
