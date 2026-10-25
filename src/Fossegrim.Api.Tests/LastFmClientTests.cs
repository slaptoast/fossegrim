using Fossegrim.Lib.Services;

namespace Fossegrim.Api.Tests;

public class LastFmClientTests
{
    // Parameters sorted by name, concatenated as <name><value>, secret
    // appended, MD5 hex. format/callback/api_sig are left out.
    [Fact]
    public void Sign_FollowsLastFmSpec()
    {
        var parameters = new Dictionary<string, string>
        {
            ["token"] = "tok1",
            ["method"] = "auth.getSession",
            ["api_key"] = "fakekey",
            ["format"] = "json",
        };

        var signature = LastFmClient.Sign(parameters, "fakesecret");

        // md5("api_keyfakekeymethodauth.getSessiontokentok1fakesecret")
        Assert.Equal("9b6b3af74520598995bb25b053cd65fc", signature);
    }

    [Theory]
    [InlineData(30, 30, false)]   // 30s or shorter never counts
    [InlineData(31, 16, true)]    // half of a short track
    [InlineData(31, 15, false)]
    [InlineData(200, 99, false)]  // under half
    [InlineData(200, 100, true)]  // exactly half
    [InlineData(600, 239, false)] // long track: 4 minutes caps the threshold
    [InlineData(600, 240, true)]
    public void IsScrobbleEligible_AppliesHalfOrFourMinutesRule(int durationSeconds, int playedSeconds, bool expected)
    {
        Assert.Equal(expected, LastFmClient.IsScrobbleEligible(TimeSpan.FromSeconds(durationSeconds), TimeSpan.FromSeconds(playedSeconds)));
    }
}
