using Fossegrim.Lib.Services;

namespace Fossegrim.Api.Tests;

public class ListenRuleTests
{
    [Theory]
    [InlineData(30, 30, false)]   // 30s or shorter never counts
    [InlineData(180, 90, false)]  // half: enough for Last.fm, not for us
    [InlineData(180, 156, false)] // under 90% even with the 5s tolerance
    [InlineData(180, 157, true)]  // 90% (162s) less the 5s tolerance
    [InlineData(180, 162, true)]  // exactly 90%
    [InlineData(600, 240, false)] // no 4-minute cap on long tracks
    [InlineData(600, 535, true)]
    public void IsListen_Requires90PercentOfTheTrack(int trackSeconds, int listenedSeconds, bool expected)
    {
        Assert.Equal(expected, ListenRule.IsListen(TimeSpan.FromSeconds(trackSeconds), TimeSpan.FromSeconds(listenedSeconds)));
    }
}
