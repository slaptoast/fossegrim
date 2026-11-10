using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Services.Library;
using Microsoft.Extensions.Time.Testing;

namespace Fossegrim.Api.Tests;

public class ScanJobEstimateTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly ScanJob _job;

    public ScanJobEstimateTests()
    {
        _job = new ScanJob(Guid.NewGuid(), new ScanJobSpec([], false, "Everything", "admin"), _time);
        _job.Report(new ScanProgress(ScanPhase.Counting, 0, 1000));
        _job.Report(new ScanProgress(ScanPhase.Reading, 0, 1000));
    }

    private void Read(int processed, double afterSeconds)
    {
        _time.Advance(TimeSpan.FromSeconds(afterSeconds));
        _job.Report(new ScanProgress(ScanPhase.Reading, processed, 1000));
    }

    [Fact]
    public void NoEstimate_ForTheFirstFiveSeconds()
    {
        Read(100, 4.9);

        Assert.Null(_job.ToDto().SecondsLeft);
    }

    [Fact]
    public void FiveSecondsIn_EstimatesFromTheRateSoFar()
    {
        Read(100, 5);   // 100 files in 5 s: 1000 take 50 s

        Assert.Equal(45, _job.ToDto().SecondsLeft);
    }

    [Fact]
    public void Estimate_HoldsBetweenCheckpoints_AndCountsDown()
    {
        Read(100, 5);
        Read(110, 3);   // 8 s in, slower, but no new estimate until 10 s

        Assert.Equal(42, _job.ToDto().SecondsLeft);
    }

    [Fact]
    public void Estimate_IsRedoneAt10_30And60Seconds_ThenEveryMinute()
    {
        Read(100, 5);
        Read(100, 5);   // 10 s: 100 files in 10 s, 1000 take 100 s
        Assert.Equal(90, _job.ToDto().SecondsLeft);

        Read(300, 20);  // 30 s: 300 files, 1000 take 100 s
        Assert.Equal(70, _job.ToDto().SecondsLeft);

        Read(400, 30);  // 60 s: 400 files, 1000 take 150 s
        Assert.Equal(90, _job.ToDto().SecondsLeft);

        Read(450, 30);  // 90 s: between minute checkpoints, so still the 60 s estimate
        Assert.Equal(60, _job.ToDto().SecondsLeft);

        Read(600, 30);  // 120 s: 600 files, 1000 take 200 s
        Assert.Equal(80, _job.ToDto().SecondsLeft);
    }

    [Fact]
    public void Checkpoint_WaitsForTheFirstFile()
    {
        Read(0, 6);
        Assert.Null(_job.ToDto().SecondsLeft);

        Read(60, 0);    // 60 files in 6 s: 1000 take 100 s
        Assert.Equal(94, _job.ToDto().SecondsLeft);
    }

    [Fact]
    public void NoEstimate_OnceReadingIsDone()
    {
        Read(100, 5);
        _job.Report(new ScanProgress(ScanPhase.Saving, 1000, 1000));

        Assert.Null(_job.ToDto().SecondsLeft);
    }

    [Fact]
    public void Saved_IsTheHighestReported()
    {
        _job.Report(new ScanProgress(ScanPhase.Reading, 10, 1000, Saved: 200));
        _job.Report(new ScanProgress(ScanPhase.Reading, 5, 1000, Saved: 0));   // a late report from a reader

        Assert.Equal(200, _job.ToDto().Saved);
    }
}
