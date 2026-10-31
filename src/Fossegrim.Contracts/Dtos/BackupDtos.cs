using System.Text.Json.Serialization;

namespace Fossegrim.Contracts.Dtos;

[JsonConverter(typeof(JsonStringEnumConverter<BackupFrequency>))]
public enum BackupFrequency
{
    // Every IntervalHours hours, counted from the last backup.
    Interval,
    // Every day at TimeOfDay.
    Daily,
    // On each of DaysOfWeek at TimeOfDay.
    Weekly,
    // On DayOfMonth at TimeOfDay; days past the end of a short month run on its last day.
    Monthly
}

// The automatic backup schedule. Only the fields for the chosen Frequency
// matter; the rest are kept so switching back and forth doesn't lose them.
// TimeOfDay is in TimeZone (an IANA id; blank means UTC). KeepLast and
// MaxAgeDays prune old backups after each successful one (null = no limit);
// the newest backup is never pruned.
public record BackupScheduleDto(
    bool Enabled,
    BackupFrequency Frequency,
    int IntervalHours,
    TimeOnly TimeOfDay,
    IReadOnlyList<DayOfWeek> DaysOfWeek,
    int DayOfMonth,
    string TimeZone,
    int? KeepLast,
    int? MaxAgeDays)
{
    public static BackupScheduleDto Default { get; } =
        new(false, BackupFrequency.Daily, 24, new TimeOnly(3, 0), [DayOfWeek.Sunday], 1, "", 7, null);
}

// Trigger is "manual" or "scheduled".
public record BackupRunDto(DateTime StartedAt, string Trigger, bool Success, string? FileName, long? SizeBytes, string? Error);

public record BackupFileDto(string FileName, DateTime CreatedAt, long SizeBytes);

// Directory is the folder backups are written to, as the server sees it
// (/backups in Docker). NextRunAt is null when the schedule is off.
public record BackupStatusDto(
    string Directory,
    BackupScheduleDto Schedule,
    DateTime? NextRunAt,
    BackupRunDto? LastRun,
    bool Running,
    IReadOnlyList<BackupFileDto> Backups);
