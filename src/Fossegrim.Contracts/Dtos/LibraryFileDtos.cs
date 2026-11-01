using System.Text.Json.Serialization;

namespace Fossegrim.Contracts.Dtos;

[JsonConverter(typeof(JsonStringEnumConverter<RenameStatus>))]
public enum RenameStatus
{
    // Already named as the template says.
    Unchanged,
    // Will be (or was) moved to NewPath.
    Rename,
    // Can't be moved: another file is already there, or two tracks would get the same name.
    Conflict,
    // Not inside any configured media folder, so there's nothing to name it relative to.
    NotInMediaFolder,
    FileMissing,
    // Apply only: the move itself failed (see Message).
    Failed
}

// FolderPath is the media folder the file is in (null if none), so paths
// can be shown relative to it.
public record RenameEntryDto(
    Guid MediaItemId,
    string? Title,
    string? ArtistName,
    string? Album,
    string CurrentPath,
    string? NewPath,
    RenameStatus Status,
    string? Message,
    string? FolderPath = null);

// Entries leaves out Unchanged tracks (only counted), so a library-wide
// preview stays small.
public record RenamePlanDto(string Template, int UnchangedCount, IReadOnlyList<RenameEntryDto> Entries);

// MediaItemIds, or All for the whole library.
public record RenameRequest(IReadOnlyList<Guid>? MediaItemIds = null, bool All = false);

// Problems: every track that was skipped or failed, with why.
public record RenameResultDto(int Renamed, int UnchangedCount, IReadOnlyList<RenameEntryDto> Problems);

// Example: the template applied to a sample track, for the settings page.
public record RenameTemplateDto(string Template, string Example);

public record UpdateRenameTemplateRequest(string Template);

// A track whose ID3v1 and ID3v2 tags disagree; Fields names which ones.
public record TagVersionMismatchDto(Guid MediaItemId, string? Title, string? ArtistName, string? Album, string Path, IReadOnlyList<string> Fields);

public record FixTagVersionsRequest(IReadOnlyList<Guid> MediaItemIds);

public record FixTagVersionsResultDto(int Fixed, IReadOnlyList<MediaItemTagUpdateError> Errors);

// Counts for the Library health page's tabs.
public record LibraryHealthDto(int TagVersionMismatches, int FileNameMismatches, int NotInMediaFolder, int FileMissing);
