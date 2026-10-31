namespace Fossegrim.Contracts.Dtos;

public record VersionDto(string Version);

// Admin view of the update check. LatestVersion is the newest stable release
// published to Docker Hub, kept from the last successful check (Error is set
// when the most recent attempt failed). CanCheck is false for development
// builds ("dev" etc.), which have no release version to compare.
public record UpdateStatusDto(
    string CurrentVersion,
    bool CanCheck,
    bool Enabled,
    string? LatestVersion,
    bool UpdateAvailable,
    DateTime? CheckedAt,
    string? Error,
    string? ReleaseUrl);

public record UpdateCheckSettingsRequest(bool Enabled);
