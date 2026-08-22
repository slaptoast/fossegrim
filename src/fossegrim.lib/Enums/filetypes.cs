namespace Fossegrim.Lib.Enums;

/// <summary>
/// Enum containing common media file types
/// </summary>
public enum MediaFileType
{
    // Video formats
    MP4,
    AVI,
    MKV,
    MOV,
    WMV,
    FLV,
    WEBM,
    MPEG,
    MPG,
    M4V,
    VOB,
    OGV,
    TS,
    MTS,
    M2TS,
    _3GP,
    _3G2,
    ASF,
    RM,
    RMVB,
    F4V,
    SWF,
    DIVX,

    // Audio formats
    MP3,
    WAV,
    FLAC,
    AAC,
    OGG,
    WMA,
    M4A,
    ALAC,
    APE,
    AIFF,
    OPUS,
    AMR,
    AC3,
    DTS,
    MKA,
    MID,
    MIDI,
    RA,

    // Image formats
    JPG,
    JPEG,
    PNG,
    GIF,
    BMP,
    TIFF,
    TIF,
    WEBP,
    SVG,
    ICO,
    HEIC,
    HEIF,
    RAW,
    CR2,
    NEF,
    ARW,
    DNG,
    ORF,
    RW2,
    PEF,
    SR2,
    RAF,

    // Document formats (often contain media)
    PDF,

    // Unknown or unsupported
    Unknown
}

/// <summary>
/// Helper class for working with media file types
/// </summary>
public static class MediaFileTypeHelper
{
    /// <summary>
    /// Gets the MediaFileType from a file extension
    /// </summary>
    /// <param name="extension">File extension (with or without the leading dot)</param>
    /// <returns>The corresponding MediaFileType</returns>
    public static MediaFileType GetFileType(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return MediaFileType.Unknown;

        // Remove leading dot if present
        extension = extension.TrimStart('.').ToUpperInvariant();

        // Handle special cases for enums that start with numbers
        if (extension == "3GP")
            return MediaFileType._3GP;
        if (extension == "3G2")
            return MediaFileType._3G2;

        if (Enum.TryParse<MediaFileType>(extension, true, out var fileType))
            return fileType;

        return MediaFileType.Unknown;
    }

    /// <summary>
    /// Gets the file extension string from a MediaFileType
    /// </summary>
    /// <param name="fileType">The MediaFileType</param>
    /// <returns>The file extension (lowercase, without dot)</returns>
    public static string GetExtension(MediaFileType fileType)
    {
        return fileType switch
        {
            MediaFileType._3GP => "3gp",
            MediaFileType._3G2 => "3g2",
            MediaFileType.Unknown => "",
            _ => fileType.ToString().ToLowerInvariant()
        };
    }

    /// <summary>
    /// Determines if a file extension represents a video file
    /// </summary>
    public static bool IsVideo(MediaFileType fileType)
    {
        return fileType switch
        {
            MediaFileType.MP4 or MediaFileType.AVI or MediaFileType.MKV or
            MediaFileType.MOV or MediaFileType.WMV or MediaFileType.FLV or
            MediaFileType.WEBM or MediaFileType.MPEG or MediaFileType.MPG or
            MediaFileType.M4V or MediaFileType.VOB or MediaFileType.OGV or
            MediaFileType.TS or MediaFileType.MTS or MediaFileType.M2TS or
            MediaFileType._3GP or MediaFileType._3G2 or MediaFileType.ASF or
            MediaFileType.RM or MediaFileType.RMVB or MediaFileType.F4V or
            MediaFileType.SWF or MediaFileType.DIVX => true,
            _ => false
        };
    }

    /// <summary>
    /// Determines if a file extension represents an audio file
    /// </summary>
    public static bool IsAudio(MediaFileType fileType)
    {
        return fileType switch
        {
            MediaFileType.MP3 or MediaFileType.WAV or MediaFileType.FLAC or
            MediaFileType.AAC or MediaFileType.OGG or MediaFileType.WMA or
            MediaFileType.M4A or MediaFileType.ALAC or MediaFileType.APE or
            MediaFileType.AIFF or MediaFileType.OPUS or MediaFileType.AMR or
            MediaFileType.AC3 or MediaFileType.DTS or MediaFileType.MKA or
            MediaFileType.MID or MediaFileType.MIDI or MediaFileType.RA => true,
            _ => false
        };
    }

    /// <summary>
    /// Determines if a file extension represents an image file
    /// </summary>
    public static bool IsImage(MediaFileType fileType)
    {
        return fileType switch
        {
            MediaFileType.JPG or MediaFileType.JPEG or MediaFileType.PNG or
            MediaFileType.GIF or MediaFileType.BMP or MediaFileType.TIFF or
            MediaFileType.TIF or MediaFileType.WEBP or MediaFileType.SVG or
            MediaFileType.ICO or MediaFileType.HEIC or MediaFileType.HEIF or
            MediaFileType.RAW or MediaFileType.CR2 or MediaFileType.NEF or
            MediaFileType.ARW or MediaFileType.DNG or MediaFileType.ORF or
            MediaFileType.RW2 or MediaFileType.PEF or MediaFileType.SR2 or
            MediaFileType.RAF => true,
            _ => false
        };
    }
}
