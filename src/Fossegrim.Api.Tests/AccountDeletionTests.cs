using System.Net;
using System.Net.Http.Json;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fossegrim.Api.Tests;

/// <summary>Admins deleting accounts (a mark, not a removal) and restoring them.</summary>
public class AccountDeletionTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;

    public AccountDeletionTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Deleting_SignsOutEverywhere_AndBlocksSigningIn()
    {
        var admin = await _factory.CreateAdminAsync();
        var user = await _factory.CreateUserAsync();
        var refreshToken = await SignInAgainAsync(user);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteAsync($"/api/admin/users/{user.UserId}")).StatusCode);

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, user.Password))).StatusCode);
    }

    [Fact]
    public async Task DeletedAccounts_AreOnlyListedAsDeleted_AndCantBeFoundOrSharedWith()
    {
        var admin = await _factory.CreateAdminAsync();
        var user = await _factory.CreateUserAsync();
        var other = await _factory.CreateUserAsync();
        var displayName = (await admin.Client.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users"))!.Single(u => u.UserId == user.UserId).UserName;

        await admin.Client.DeleteAsync($"/api/admin/users/{user.UserId}");

        Assert.DoesNotContain(await admin.Client.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users") ?? [], u => u.UserId == user.UserId);
        var deleted = (await admin.Client.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users?deleted=true"))!.Single(u => u.UserId == user.UserId);
        Assert.NotNull(deleted.DeletedAt);

        Assert.Empty(await other.Client.GetFromJsonAsync<List<UserSummaryDto>>($"/api/users/search?q={Uri.EscapeDataString(displayName)}") ?? []);
        Assert.Empty(await other.Client.GetFromJsonAsync<List<UserSummaryDto>>($"/api/users/search?q={Uri.EscapeDataString(user.Email)}") ?? []);

        var playlist = await CreatePlaylistAsync(other, "Shared mix");
        var share = await other.Client.PostAsJsonAsync($"/api/playlists/{playlist.Id}/shares", new SharePlaylistRequest(user.UserId, "ReadOnly"));
        Assert.Equal(HttpStatusCode.BadRequest, share.StatusCode);
    }

    [Fact]
    public async Task Restoring_BringsTheAccountBack_WithItsData()
    {
        var admin = await _factory.CreateAdminAsync();
        var user = await _factory.CreateUserAsync();
        var playlist = await CreatePlaylistAsync(user, "Kept through deletion");

        await admin.Client.DeleteAsync($"/api/admin/users/{user.UserId}");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.PostAsync($"/api/admin/users/{user.UserId}/restore", null)).StatusCode);

        Assert.Contains(await admin.Client.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users") ?? [], u => u.UserId == user.UserId && u.DeletedAt == null);
        Assert.DoesNotContain(await admin.Client.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users?deleted=true") ?? [], u => u.UserId == user.UserId);

        var signedIn = await SignInAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync($"/api/playlists/{playlist.Id}")).StatusCode);

        // Only a deleted account can be restored.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.PostAsync($"/api/admin/users/{user.UserId}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task ResetLinks_StopWorking_AndCantBeMade_ForADeletedAccount()
    {
        var admin = await _factory.CreateAdminAsync();
        var user = await _factory.CreateUserAsync();
        var link = await (await admin.Client.PostAsync($"/api/admin/users/{user.UserId}/reset-link", null)).Content.ReadFromJsonAsync<PasswordResetLinkDto>();
        var (userId, token) = PasswordResetTests.ParseLink(link!.Path, "");

        await admin.Client.DeleteAsync($"/api/admin/users/{user.UserId}");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.PostAsync($"/api/admin/users/{user.UserId}/reset-link", null)).StatusCode);
        var reset = await _factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(userId, token, "N3w-passw0rd!"));
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);

        // Still void after restoring: deleting changed the account's security stamp.
        await admin.Client.PostAsync($"/api/admin/users/{user.UserId}/restore", null);
        reset = await _factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(userId, token, "N3w-passw0rd!"));
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
    }

    [Fact]
    public async Task AddingAnAccount_WithADeletedAccountsEmail_SaysToRestoreIt()
    {
        var admin = await _factory.CreateAdminAsync();
        var user = await _factory.CreateUserAsync();
        await admin.Client.DeleteAsync($"/api/admin/users/{user.UserId}");

        var response = await admin.Client.PostAsJsonAsync("/api/admin/users", new CreateUserRequest(user.Email, $"Again {Guid.NewGuid():N}", "Passw0rd!x", false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("deleted account", Assert.Single((await response.Content.ReadFromJsonAsync<IdentityErrorsResponse>())!.Errors));
    }

    [Fact]
    public async Task AdminsCantDeleteThemselves()
    {
        var admin = await _factory.CreateAdminAsync();

        var response = await admin.Client.DeleteAsync($"/api/admin/users/{admin.UserId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("You can't delete your own account.", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error);
    }

    [Fact]
    public async Task OnlyAdminsCanDelete_AndUnknownOrDeletedAccountsAreNotFound()
    {
        var admin = await _factory.CreateAdminAsync();
        var user = await _factory.CreateUserAsync();
        var other = await _factory.CreateUserAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.DeleteAsync($"/api/admin/users/{other.UserId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.PostAsync($"/api/admin/users/{other.UserId}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.DeleteAsync("/api/admin/users/no-such-user")).StatusCode);

        await admin.Client.DeleteAsync($"/api/admin/users/{other.UserId}");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.DeleteAsync($"/api/admin/users/{other.UserId}")).StatusCode);
    }

    [Fact]
    public async Task TheLastAdmin_CantBeDeleted()
    {
        // Its own database, so these are the only admins.
        using var factory = new FossegrimApiFactory();
        var first = await factory.CreateAdminAsync();
        var second = await factory.CreateAdminAsync();

        // The first admin's token still says Admin (roles are read at sign-in),
        // but the role is gone, so the second is the only real admin left.
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await users.RemoveFromRoleAsync((await users.FindByIdAsync(first.UserId))!, "Admin");
        }

        var response = await first.Client.DeleteAsync($"/api/admin/users/{second.UserId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("only admin", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error);
        await factory.WithDbAsync(async db =>
            Assert.Null(await db.Users.Where(u => u.Id == second.UserId).Select(u => u.DeletedAt).SingleAsync()));
    }

    [Fact]
    public async Task AnotherAdmin_CanBeDeleted_WhileOneIsLeft()
    {
        using var factory = new FossegrimApiFactory();
        var first = await factory.CreateAdminAsync();
        var second = await factory.CreateAdminAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await first.Client.DeleteAsync($"/api/admin/users/{second.UserId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.Client.GetAsync("/api/auth/me")).StatusCode);
    }

    private async Task<HttpClient> SignInAsync(TestUser user)
    {
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, user.Password))).Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login!.Token);
        return client;
    }

    // A second device, returning its refresh token.
    private async Task<string> SignInAgainAsync(TestUser user)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, user.Password));
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.RefreshToken!;
    }

    private static async Task<PlaylistDto> CreatePlaylistAsync(TestUser owner, string name)
    {
        var response = await owner.Client.PostAsJsonAsync("/api/playlists", new CreatePlaylistRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PlaylistDto>())!;
    }
}
