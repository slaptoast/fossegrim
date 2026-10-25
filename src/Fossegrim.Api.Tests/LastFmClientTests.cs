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
}
