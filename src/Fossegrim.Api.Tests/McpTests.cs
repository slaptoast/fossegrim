using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Fossegrim.Lib.Services.Library;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Fossegrim.Api.Tests;

/// <summary>The MCP endpoint (/mcp): its admin switch, personal access tokens, and the tools, driven through the SDK's own client.</summary>
public sealed class McpTests : IClassFixture<FossegrimApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FossegrimApiFactory _factory;
    private readonly string _tag = "m" + Guid.NewGuid().ToString("N")[..10];

    public McpTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Mcp_IsNotThere_UntilAnAdminTurnsItOn_AndTokensWaitForItToo()
    {
        var admin = await _factory.CreateAdminAsync();
        await SetEnabledAsync(admin, false);
        var user = await _factory.CreateUserAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await PostMcpAsync(null)).StatusCode);
        var list = await user.Client.GetFromJsonAsync<AccessTokenListDto>("/api/access-tokens");
        Assert.False(list!.McpEnabled);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PostAsJsonAsync("/api/access-tokens", new CreateAccessTokenRequest("Claude"))).StatusCode);

        await SetEnabledAsync(admin, true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync(null)).StatusCode);
        Assert.True((await user.Client.GetFromJsonAsync<AccessTokenListDto>("/api/access-tokens"))!.McpEnabled);

        // Turning it off again keeps tokens, but /mcp goes away.
        var token = await CreateTokenAsync(user);
        await SetEnabledAsync(admin, false);
        Assert.Equal(HttpStatusCode.NotFound, (await PostMcpAsync(token)).StatusCode);
        Assert.Single((await user.Client.GetFromJsonAsync<AccessTokenListDto>("/api/access-tokens"))!.Tokens);
        await SetEnabledAsync(admin, true);
        Assert.Equal(HttpStatusCode.OK, (await PostMcpAsync(token)).StatusCode);
    }

    [Fact]
    public async Task OnlyAdmins_FlipTheSwitch()
    {
        var user = await _factory.CreateUserClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync("/api/admin/mcp", new McpSettingsDto(true))).StatusCode);
    }

    [Fact]
    public async Task Tokens_WorkOnlyOnMcp_AndOtherCredentialsDontWorkThere()
    {
        await TurnOnAsync();
        var user = await _factory.CreateUserAsync();
        var token = await CreateTokenAsync(user);

        // A personal token can't reach the rest of the API...
        var withToken = _factory.CreateClient();
        withToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withToken.GetAsync("/api/playlists")).StatusCode);

        // ...and the web app's access token can't reach /mcp.
        var jwt = user.Client.DefaultRequestHeaders.Authorization!.Parameter!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync(jwt)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync("fsgp_not-a-real-token")).StatusCode);
    }

    [Fact]
    public async Task Tokens_AreListedWithoutTheSecret_AndRevokingOneStopsIt()
    {
        await TurnOnAsync();
        var user = await _factory.CreateUserAsync();
        var response = await user.Client.PostAsJsonAsync("/api/access-tokens", new CreateAccessTokenRequest("  Claude Desktop "));
        var created = (await response.Content.ReadFromJsonAsync<CreatedAccessTokenDto>())!;
        Assert.StartsWith("fsgp_", created.Secret);
        Assert.Equal("Claude Desktop", created.Token.Name);
        Assert.Equal(created.Secret[..12], created.Token.Prefix);

        Assert.Equal(HttpStatusCode.OK, (await PostMcpAsync(created.Secret)).StatusCode);
        var listed = (await user.Client.GetFromJsonAsync<AccessTokenListDto>("/api/access-tokens"))!.Tokens.Single();
        Assert.NotNull(listed.LastUsedAt);
        Assert.DoesNotContain(created.Secret, await user.Client.GetStringAsync("/api/access-tokens"));

        // Someone else can't revoke it.
        var other = await _factory.CreateUserClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/access-tokens/{created.Token.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await user.Client.DeleteAsync($"/api/access-tokens/{created.Token.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync(created.Secret)).StatusCode);
    }

    [Fact]
    public async Task BlankTokenNames_AreRefused()
    {
        await TurnOnAsync();
        var user = await _factory.CreateUserClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync("/api/access-tokens", new CreateAccessTokenRequest(" "))).StatusCode);
    }

    [Fact]
    public async Task DeletedAccounts_AndPasswordResets_StopTokens()
    {
        await TurnOnAsync();
        var admin = await _factory.CreateAdminAsync();

        var deleted = await _factory.CreateUserAsync();
        var deletedToken = await CreateTokenAsync(deleted);
        (await admin.Client.DeleteAsync($"/api/admin/users/{deleted.UserId}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync(deletedToken)).StatusCode);

        var reset = await _factory.CreateUserAsync();
        var resetToken = await CreateTokenAsync(reset);
        var link = await admin.Client.PostAsync($"/api/admin/users/{reset.UserId}/reset-link", null);
        var (userId, token) = PasswordResetTests.ParseLink((await link.Content.ReadFromJsonAsync<PasswordResetLinkDto>())!.Path, "");
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(userId, token, "N3w-passw0rd!"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync(resetToken)).StatusCode);
    }

    [Fact]
    public async Task Client_SeesTheTools_AndSearchesTheLibrary()
    {
        await TurnOnAsync();
        var track = await _factory.AddMediaItemAsync(title: $"{_tag} Anthem", artist: "Searchers", album: "Found", linkedArtistName: "Searchers", linkedAlbumLabel: "Label");
        await _factory.WithDbAsync(db => new SearchIndex(db).RebuildAsync());
        await using var client = await ConnectAsync(await _factory.CreateUserAsync());

        Assert.Equal("Fossegrim", client.ServerInfo.Name);
        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("search_library", tools);
        Assert.Contains("create_playlist", tools);
        Assert.DoesNotContain(tools, t => t.Contains("scan") || t.Contains("admin"));

        var results = await CallAsync(client, "search_library", new() { ["query"] = _tag });
        Assert.Equal(track.Id, results.GetProperty("tracks")[0].GetProperty("id").GetGuid());
        Assert.Equal("3:00", results.GetProperty("tracks")[0].GetProperty("duration").GetString());
    }

    [Fact]
    public async Task Favorites_AreTheTokenOwners()
    {
        await TurnOnAsync();
        var track = await _factory.AddMediaItemAsync(title: $"{_tag} Loved");
        var me = await _factory.CreateUserAsync();
        await using var client = await ConnectAsync(me);

        await CallAsync(client, "set_favorite", new() { ["kind"] = "tracks", ["id"] = track.Id });

        var favorites = await me.Client.GetFromJsonAsync<FavoritesDto>("/api/favorites");
        Assert.Equal([track.Id], favorites!.TrackIds);
        var listed = await CallAsync(client, "list_tracks", new() { ["favoritesOnly"] = true });
        Assert.Equal(1, listed.GetProperty("totalCount").GetInt32());

        var error = await CallForErrorAsync(client, "set_favorite", new() { ["kind"] = "songs", ["id"] = track.Id });
        Assert.Contains("tracks, albums or artists", error);
    }

    [Fact]
    public async Task Playlists_CanBeMadeAndEdited_AsTheTokenOwner()
    {
        await TurnOnAsync();
        var first = await _factory.AddMediaItemAsync(title: "One");
        var second = await _factory.AddMediaItemAsync(title: "Two");
        var me = await _factory.CreateUserAsync();
        await using var client = await ConnectAsync(me);

        var created = await CallAsync(client, "create_playlist", new() { ["name"] = "Road trip", ["trackIds"] = new[] { first.Id } });
        var playlistId = created.GetProperty("id").GetGuid();
        await CallAsync(client, "add_to_playlist", new() { ["playlistId"] = playlistId, ["trackIds"] = new[] { second.Id, first.Id } });

        var playlist = await CallAsync(client, "get_playlist", new() { ["playlistId"] = playlistId });
        var entries = playlist.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(["One", "Two", "One"], entries.Select(e => e.GetProperty("title").GetString()));

        // Remove the second "One" by its entry id.
        await CallAsync(client, "remove_from_playlist", new() { ["playlistId"] = playlistId, ["entryIds"] = new[] { entries[2].GetProperty("entryId").GetGuid() } });
        await CallAsync(client, "rename_playlist", new() { ["playlistId"] = playlistId, ["name"] = "Road trip 2" });

        var viaApi = await me.Client.GetFromJsonAsync<PlaylistDetailDto>($"/api/playlists/{playlistId}");
        Assert.Equal("Road trip 2", viaApi!.Name);
        Assert.Equal([first.Id, second.Id], viaApi.Items.Select(i => i.MediaItem.Id));

        Assert.Contains("Unknown track", await CallForErrorAsync(client, "create_playlist", new() { ["name"] = "Bad", ["trackIds"] = new[] { Guid.NewGuid() } }));
        Assert.DoesNotContain(await me.Client.GetFromJsonAsync<List<PlaylistDto>>("/api/playlists") ?? [], p => p.Name == "Bad");

        await CallAsync(client, "delete_playlist", new() { ["playlistId"] = playlistId });
        Assert.Equal(HttpStatusCode.NotFound, (await me.Client.GetAsync($"/api/playlists/{playlistId}")).StatusCode);
    }

    [Fact]
    public async Task OtherPeoplesPlaylists_FollowTheirSharing()
    {
        await TurnOnAsync();
        var track = await _factory.AddMediaItemAsync(title: "Shared");
        var owner = await _factory.CreateUserAsync();
        var me = await _factory.CreateUserAsync();
        await using var client = await ConnectAsync(me);

        var hidden = await CreatePlaylistAsync(owner, "Private");
        Assert.Contains("No playlist", await CallForErrorAsync(client, "get_playlist", new() { ["playlistId"] = hidden }));

        var readOnly = await CreatePlaylistAsync(owner, "Look only");
        (await owner.Client.PostAsJsonAsync($"/api/playlists/{readOnly}/shares", new SharePlaylistRequest(me.UserId, "ReadOnly"))).EnsureSuccessStatusCode();
        await CallAsync(client, "get_playlist", new() { ["playlistId"] = readOnly });
        Assert.Contains("read-only", await CallForErrorAsync(client, "add_to_playlist", new() { ["playlistId"] = readOnly, ["trackIds"] = new[] { track.Id } }));

        var editable = await CreatePlaylistAsync(owner, "Together");
        (await owner.Client.PostAsJsonAsync($"/api/playlists/{editable}/shares", new SharePlaylistRequest(me.UserId, "Editor"))).EnsureSuccessStatusCode();
        await CallAsync(client, "add_to_playlist", new() { ["playlistId"] = editable, ["trackIds"] = new[] { track.Id } });
        Assert.Contains("owner", await CallForErrorAsync(client, "delete_playlist", new() { ["playlistId"] = editable }));

        var mine = (await CallAsync(client, "list_playlists", [])).EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Look only", "Together"], mine);
    }

    [Fact]
    public async Task PlayHistory_IsTheTokenOwners()
    {
        await TurnOnAsync();
        var track = await _factory.AddMediaItemAsync(title: $"{_tag} Heard");
        var me = await _factory.CreateUserAsync();
        var someoneElse = await _factory.CreateUserAsync();
        await _factory.WithDbAsync(async db =>
        {
            db.PlayEvents.Add(new PlayEvent { Id = Guid.NewGuid(), UserId = me.UserId, MediaItemId = track.Id, PlayedAt = DateTime.UtcNow.AddMinutes(-5) });
            db.PlayEvents.Add(new PlayEvent { Id = Guid.NewGuid(), UserId = someoneElse.UserId, MediaItemId = track.Id, PlayedAt = DateTime.UtcNow.AddMinutes(-5) });
            await db.SaveChangesAsync();
        });
        await using var client = await ConnectAsync(me);

        var history = await CallAsync(client, "get_play_history", new() { ["query"] = _tag });
        Assert.Equal(1, history.GetProperty("totalCount").GetInt32());
        Assert.Equal(track.Id, history.GetProperty("items")[0].GetProperty("trackId").GetGuid());
        Assert.Equal(1, (await CallAsync(client, "get_library_stats", [])).GetProperty("yourPlays").GetInt32());
    }

    private async Task TurnOnAsync() => await SetEnabledAsync(await _factory.CreateAdminAsync(), true);

    private static async Task SetEnabledAsync(TestUser admin, bool enabled) =>
        (await admin.Client.PutAsJsonAsync("/api/admin/mcp", new McpSettingsDto(enabled))).EnsureSuccessStatusCode();

    private static async Task<string> CreateTokenAsync(TestUser user)
    {
        var response = await user.Client.PostAsJsonAsync("/api/access-tokens", new CreateAccessTokenRequest("Assistant"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedAccessTokenDto>())!.Secret;
    }

    private static async Task<Guid> CreatePlaylistAsync(TestUser user, string name)
    {
        var response = await user.Client.PostAsJsonAsync("/api/playlists", new CreatePlaylistRequest(name));
        return (await response.Content.ReadFromJsonAsync<PlaylistDto>())!.Id;
    }

    // An MCP "ping" with a bearer token (or none), checking only the HTTP status.
    private async Task<HttpResponseMessage> PostMcpAsync(string? token)
    {
        var http = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await http.SendAsync(request);
    }

    private async Task<McpClient> ConnectAsync(TestUser user)
    {
        var token = await CreateTokenAsync(user);
        var http = _factory.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.True(result.IsError is not true, $"{tool} failed: {text}");
        // Most tools answer with JSON; a few with a sentence.
        return text.TrimStart() is ['{' or '[', ..]
            ? JsonSerializer.Deserialize<JsonElement>(text, Json)
            : JsonSerializer.SerializeToElement(text);
    }

    private static async Task<string> CallForErrorAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError, $"{tool} should have failed");
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
