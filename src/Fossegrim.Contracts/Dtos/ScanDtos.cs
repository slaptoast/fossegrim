using System.Text.Json.Serialization;

namespace Fossegrim.Contracts.Dtos;

[JsonConverter(typeof(JsonStringEnumConverter<ScanJobState>))]
public enum ScanJobState { Queued, Running, Completed, Failed }

[JsonConverter(typeof(JsonStringEnumConverter<ScanPhase>))]
public enum ScanPhase
{
    Waiting,
    // Walking the folders to find the audio files, so progress has a total.
    Counting,
    // Reading tags from new and changed files (every file on a full scan).
    Reading,
    // Writing the results to the database.
    Saving,
    Done
}

// Moved: a file that disappeared from one path and showed up at another with
// the same size and tags; it keeps its play history and playlist entries.
// Warnings: why nothing was removed from a folder, files that couldn't be read.
public record ScanSummary(
    int FilesFound,
    int Added,
    int Updated,
    int Unchanged,
    int Moved,
    int Removed,
    int Failed,
    IReadOnlyList<string> Warnings);

// Source says who asked: "admin", "api:<key name>" or "rescan".
// Processed/Total count files in the Reading phase.
public record ScanJobDto(
    Guid Id,
    string Description,
    bool Full,
    string Source,
    ScanJobState State,
    ScanPhase Phase,
    int Processed,
    int Total,
    DateTime QueuedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    ScanSummary? Summary,
    string? Error);

public record ScanStatusDto(ScanJobDto? Current, IReadOnlyList<ScanJobDto> Queued, IReadOnlyList<ScanJobDto> Recent);

// No FolderIds and no Path scans every media folder. A Path must be inside a
// configured media folder. Full re-reads every file instead of only new and
// changed ones.
public record StartScanRequest(IReadOnlyList<Guid>? FolderIds = null, string? Path = null, bool Full = false);

// For /api/external/scan: no Path scans every media folder.
public record ExternalScanRequest(string? Path = null, bool Full = false);
