using System.Net;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Fossegrim.Api.Tests;

public class PlaylistSharingTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;

    // Display names are unique per test, since tests share one database.
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    public PlaylistSharingTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Search_FindsDisplayNames_WithoutReturningEmails()
    {
        var owner = await _factory.CreateUserAsync();
        var friend = await CreateNamedUserAsync($"Kari {_tag}");

        var response = await owner.Client.GetAsync($"/api/users/search?q={Uri.EscapeDataString("kari " + _tag)}");
        var body = await response.Content.ReadAsStringAsync();
        var results = await response.Content.ReadFromJsonAsync<List<UserSummaryDto>>();

        var match = Assert.Single(results!);
        Assert.Equal(friend.UserId, match.UserId);
        Assert.Equal($"Kari {_tag}", match.DisplayName);
        Assert.DoesNotContain("@", body);
    }

    [Fact]
    public async Task Search_FindsSomeoneByExactEmailOnly()
    {
        var owner = await _factory.CreateUserAsync();
        var friend = await CreateNamedUserAsync($"Ola {_tag}");
        var email = await EmailOfAsync(friend);

        var exact = await SearchAsync(owner, email.ToUpperInvariant());
        var partial = await SearchAsync(owner, email[..^4]); // the email minus ".com": not a display name match

        Assert.Equal(friend.UserId, Assert.Single(exact).UserId);
        Assert.Empty(partial);
    }

    [Fact]
    public async Task Search_LeavesOutTheSearcherAndShortQueries()
    {
        var me = await CreateNamedUserAsync($"Self {_tag}");

        Assert.Empty(await SearchAsync(me, $"Self {_tag}"));
        Assert.Empty(await SearchAsync(me, "S"));
    }

    [Fact]
    public async Task Search_TreatsWildcardsLiterally()
    {
        var owner = await _factory.CreateUserAsync();
        await CreateNamedUserAsync($"Anne {_tag}");

        Assert.Empty(await SearchAsync(owner, $"%{_tag}%x"));
    }

    [Fact]
    public async Task Search_RequiresSignIn()
    {
        var response = await _factory.CreateClient().GetAsync("/api/users/search?q=anyone");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SharedUser_SeesThePlaylist_AndCanLeaveIt()
    {
        var owner = await _factory.CreateUserAsync();
        var friend = await CreateNamedUserAsync($"Friend {_tag}");
        var other = await CreateNamedUserAsync($"Other {_tag}");
        var playlist = await CreatePlaylistAsync(owner, "Road trip");
        await ShareAsync(owner, playlist, friend, "ReadOnly");
        await ShareAsync(owner, playlist, other, "Editor");

        Assert.Contains(await ListAsync(friend), p => p.Id == playlist.Id && p.AccessLevel == "ReadOnly");

        var leave = await friend.Client.PostAsync($"/api/playlists/{playlist.Id}/leave", null);

        Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
        Assert.DoesNotContain(await ListAsync(friend), p => p.Id == playlist.Id);
        // Only the friend's access went: the playlist and the other share remain.
        var detail = await owner.Client.GetFromJsonAsync<PlaylistDetailDto>($"/api/playlists/{playlist.Id}");
        Assert.Equal([other.UserId], detail!.Shares.Select(s => s.UserId));
    }

    [Fact]
    public async Task Owner_CannotLeaveTheirOwnPlaylist()
    {
        var owner = await _factory.CreateUserAsync();
        var playlist = await CreatePlaylistAsync(owner, "Mine");

        var leave = await owner.Client.PostAsync($"/api/playlists/{playlist.Id}/leave", null);

        Assert.Equal(HttpStatusCode.NotFound, leave.StatusCode);
        Assert.Contains(await ListAsync(owner), p => p.Id == playlist.Id);
    }

    [Fact]
    public async Task Stranger_CannotLeaveAPlaylistNotSharedWithThem()
    {
        var owner = await _factory.CreateUserAsync();
        var stranger = await _factory.CreateUserAsync();
        var playlist = await CreatePlaylistAsync(owner, "Private");

        var leave = await stranger.Client.PostAsync($"/api/playlists/{playlist.Id}/leave", null);

        Assert.Equal(HttpStatusCode.NotFound, leave.StatusCode);
    }

    [Fact]
    public async Task NewPlaylist_CanBeCreatedAndFilledFromThePicker()
    {
        // What "+ New playlist" in the Add to playlist picker does: create, then add.
        var owner = await _factory.CreateUserAsync();
        var (a, b) = (await _factory.AddMediaItemAsync("A"), await _factory.AddMediaItemAsync("B"));

        var playlist = await CreatePlaylistAsync(owner, "Fresh");
        var added = await owner.Client.PostAsJsonAsync($"/api/playlists/{playlist.Id}/items", new AddPlaylistItemsRequest([a.Id, b.Id]));

        added.EnsureSuccessStatusCode();
        var detail = await added.Content.ReadFromJsonAsync<PlaylistDetailDto>();
        Assert.Equal(["A", "B"], detail!.Items.OrderBy(i => i.Position).Select(i => i.MediaItem.Title));
    }

    private async Task<TestUser> CreateNamedUserAsync(string displayName)
    {
        var user = await _factory.CreateUserAsync();
        var response = await user.Client.PutAsJsonAsync("/api/auth/me", new UpdateProfileRequest(displayName, null));
        response.EnsureSuccessStatusCode();
        return user;
    }

    private async Task<string> EmailOfAsync(TestUser user)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await userManager.FindByIdAsync(user.UserId))!.Email!;
    }

    private static async Task<List<UserSummaryDto>> SearchAsync(TestUser user, string query) =>
        (await user.Client.GetFromJsonAsync<List<UserSummaryDto>>($"/api/users/search?q={Uri.EscapeDataString(query)}"))!;

    private static async Task<PlaylistDto> CreatePlaylistAsync(TestUser owner, string name)
    {
        var response = await owner.Client.PostAsJsonAsync("/api/playlists", new CreatePlaylistRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlaylistDto>())!;
    }

    private static async Task ShareAsync(TestUser owner, PlaylistDto playlist, TestUser with, string shareType) =>
        (await owner.Client.PostAsJsonAsync($"/api/playlists/{playlist.Id}/shares", new SharePlaylistRequest(with.UserId, shareType)))
            .EnsureSuccessStatusCode();

    private static async Task<List<PlaylistDto>> ListAsync(TestUser user) =>
        (await user.Client.GetFromJsonAsync<List<PlaylistDto>>("/api/playlists"))!;
}
