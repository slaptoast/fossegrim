using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Lib.Services;

/// <summary>
/// Works out when a <see cref="BackupScheduleDto"/> next fires. Calendar
/// schedules are evaluated in the schedule's own time zone, so "03:00" stays
/// 03:00 local across daylight saving changes.
/// </summary>
public static class BackupSchedules
{
    public const int MaxIntervalHours = 24 * 30;

    // Far enough to find any weekly or monthly slot.
    private const int SearchDays = 400;

    /// <summary>The first run strictly after <paramref name="afterUtc"/>, or null if the schedule is off or can never fire.</summary>
    public static DateTime? NextRun(BackupScheduleDto schedule, DateTime afterUtc)
    {
        if (!schedule.Enabled)
        {
            return null;
        }

        if (schedule.Frequency == BackupFrequency.Interval)
        {
            return afterUtc.AddHours(Math.Clamp(schedule.IntervalHours, 1, MaxIntervalHours));
        }

        var zone = FindTimeZone(schedule.TimeZone) ?? TimeZoneInfo.Utc;
        var startDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone));

        for (var date = startDate; date <= startDate.AddDays(SearchDays); date = date.AddDays(1))
        {
            if (!Matches(schedule, date))
            {
                continue;
            }

            var candidate = ToUtc(date.ToDateTime(schedule.TimeOfDay), zone);
            if (candidate > afterUtc)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Null when <paramref name="id"/> isn't a time zone this machine knows; blank means UTC.</summary>
    public static TimeZoneInfo? FindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static bool Matches(BackupScheduleDto schedule, DateOnly date) => schedule.Frequency switch
    {
        BackupFrequency.Daily => true,
        BackupFrequency.Weekly => schedule.DaysOfWeek.Contains(date.DayOfWeek),
        BackupFrequency.Monthly => date.Day == Math.Min(schedule.DayOfMonth, DateTime.DaysInMonth(date.Year, date.Month)),
        _ => false
    };

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        // A time skipped by a spring-forward change runs once the clocks
        // have moved on (02:30 becomes 03:30). An ambiguous fall-back time
        // runs at its first occurrence.
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        if (zone.IsAmbiguousTime(local))
        {
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, offset).UtcDateTime;
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
