using System.Text.RegularExpressions;

namespace Fossegrim.Lib.Services;

/// <summary>
/// A release version as the release workflow tags images: MAJOR.MINOR.PATCH
/// with an optional -prerelease suffix (a leading "v" is tolerated). The
/// floating "1.2" / "1" / "latest" tags and CI's "sha-..." / "pr-..." tags
/// don't parse, which is how the update check skips them.
/// </summary>
public sealed partial record ReleaseVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => Prerelease is not null;

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        var match = text is null ? null : Pattern().Match(text.Trim());
        if (match is null || !match.Success ||
            !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Value, out var patch))
        {
            version = null!;
            return false;
        }

        version = new ReleaseVersion(major, minor, patch, match.Groups[4].Success ? match.Groups[4].Value : null);
        return true;
    }

    // Prereleases sort before the release they lead up to (1.2.0-rc.1 < 1.2.0).
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
        {
            return core;
        }

        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(Prerelease, other.Prerelease)
        };
    }

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public override string ToString() => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();
}
