using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Services;

namespace Fossegrim.Api.Tests;

public class BackupScheduleTests
{
    private static readonly BackupScheduleDto Daily = BackupScheduleDto.Default with { Enabled = true, TimeOfDay = new TimeOnly(3, 0) };

    private static DateTime Utc(string text) => DateTime.Parse(text, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

    [Fact]
    public void Off_NeverRuns()
    {
        Assert.Null(BackupSchedules.NextRun(Daily with { Enabled = false }, Utc("2026-01-01T00:00:00Z")));
    }

    [Fact]
    public void Interval_CountsFromTheGivenTime()
    {
        var schedule = Daily with { Frequency = BackupFrequency.Interval, IntervalHours = 6 };
        Assert.Equal(Utc("2026-01-01T16:30:00Z"), BackupSchedules.NextRun(schedule, Utc("2026-01-01T10:30:00Z")));
    }

    [Theory]
    [InlineData("2026-01-01T02:59:00Z", "2026-01-01T03:00:00Z")] // later today
    [InlineData("2026-01-01T03:00:00Z", "2026-01-02T03:00:00Z")] // strictly after: not the same instant
    [InlineData("2026-01-01T10:00:00Z", "2026-01-02T03:00:00Z")] // tomorrow
    public void Daily_RunsAtTheTimeOfDay(string after, string expected)
    {
        Assert.Equal(Utc(expected), BackupSchedules.NextRun(Daily, Utc(after)));
    }

    [Fact]
    public void Daily_UsesTheScheduleTimeZone()
    {
        // 03:00 in Oslo is 02:00 UTC in winter and 01:00 UTC in summer.
        var schedule = Daily with { TimeZone = "Europe/Oslo" };
        Assert.Equal(Utc("2026-01-15T02:00:00Z"), BackupSchedules.NextRun(schedule, Utc("2026-01-15T00:00:00Z")));
        Assert.Equal(Utc("2026-07-15T01:00:00Z"), BackupSchedules.NextRun(schedule, Utc("2026-07-15T00:00:00Z")));
    }

    [Fact]
    public void Daily_TimeSkippedBySpringForward_RunsAnHourLater()
    {
        // US clocks jump from 02:00 to 03:00 on 2026-03-08; 02:30 doesn't exist that day.
        var schedule = Daily with { TimeOfDay = new TimeOnly(2, 30), TimeZone = "America/New_York" };
        Assert.Equal(Utc("2026-03-08T07:30:00Z"), BackupSchedules.NextRun(schedule, Utc("2026-03-08T00:00:00Z")));
    }

    [Fact]
    public void Weekly_RunsOnTheChosenDays()
    {
        // 2026-01-01 is a Thursday.
        var schedule = Daily with { Frequency = BackupFrequency.Weekly, DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Saturday] };
        Assert.Equal(Utc("2026-01-03T03:00:00Z"), BackupSchedules.NextRun(schedule, Utc("2026-01-01T12:00:00Z")));
        Assert.Equal(Utc("2026-01-05T03:00:00Z"), BackupSchedules.NextRun(schedule, Utc("2026-01-03T03:00:00Z")));
    }

    [Fact]
    public void Weekly_WithNoDays_NeverRuns()
    {
        Assert.Null(BackupSchedules.NextRun(Daily with { Frequency = BackupFrequency.Weekly, DaysOfWeek = [] }, Utc("2026-01-01T00:00:00Z")));
    }

    [Theory]
    [InlineData(15, "2026-01-20T00:00:00Z", "2026-02-15T03:00:00Z")]
    [InlineData(31, "2026-02-01T00:00:00Z", "2026-02-28T03:00:00Z")] // short month: its last day
    [InlineData(31, "2026-02-28T03:00:00Z", "2026-03-31T03:00:00Z")]
    [InlineData(30, "2028-02-01T00:00:00Z", "2028-02-29T03:00:00Z")] // leap year
    public void Monthly_ClampsToTheEndOfShortMonths(int day, string after, string expected)
    {
        var schedule = Daily with { Frequency = BackupFrequency.Monthly, DayOfMonth = day };
        Assert.Equal(Utc(expected), BackupSchedules.NextRun(schedule, Utc(after)));
    }
}
