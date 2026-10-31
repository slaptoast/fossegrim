using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Fossegrim.Api.Tests;

// Each test gets its own fresh install, since most are about the empty state.
public class SetupTests
{
    private static readonly CreateFirstAdminRequest FirstAdmin = new("owner@example.com", "Owner", "Passw0rd!x");

    [Fact]
    public async Task FreshInstall_NeedsSetup_WithNoDefaultAdminAndRegistrationClosed()
    {
        using var factory = new FossegrimApiFactory();
        var status = await factory.CreateClient().GetFromJsonAsync<SetupStatusDto>("/api/setup");

        Assert.Equal(new SetupStatusDto(SetupRequired: true, SetupComplete: false, RegistrationOpen: false), status);
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@fossegrim.local", "Admin123!"));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task FirstAdmin_CanOnlyBeCreatedOnce_AndSetupFinishesWhenTheyComplete()
    {
        using var factory = new FossegrimApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup/admin", FirstAdmin);
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.Contains("Admin", login.Roles);

        // The account exists, but the wizard isn't finished yet.
        Assert.Equal(new SetupStatusDto(false, false, false), await client.GetFromJsonAsync<SetupStatusDto>("/api/setup"));

        var again = await client.PostAsJsonAsync("/api/setup/admin", FirstAdmin with { Email = "intruder@example.com" });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/setup/complete", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var completed = await client.PostAsync("/api/setup/complete", null);
        completed.EnsureSuccessStatusCode();
        Assert.True((await completed.Content.ReadFromJsonAsync<SetupStatusDto>())!.SetupComplete);
    }

    [Fact]
    public async Task FirstAdmin_RacingRequests_CreateExactlyOneAccount()
    {
        using var factory = new FossegrimApiFactory();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            factory.CreateClient().PostAsJsonAsync("/api/setup/admin", FirstAdmin with { Email = $"owner{i}@example.com" })));

        Assert.Single(responses, r => r.IsSuccessStatusCode);
        await factory.WithDbAsync(async db => Assert.Equal(1, db.Users.Count()));
    }

    [Fact]
    public async Task FirstAdmin_WeakPassword_IsRejectedAndSetupStaysOpen()
    {
        using var factory = new FossegrimApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup/admin", FirstAdmin with { Password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty((await response.Content.ReadFromJsonAsync<IdentityErrorsResponse>())!.Errors);
        Assert.True((await client.GetFromJsonAsync<SetupStatusDto>("/api/setup"))!.SetupRequired);
    }

    [Fact]
    public async Task ExistingInstall_WithUsers_CountsAsSetUp_ButOneMidWizardDoesNot()
    {
        using var factory = new FossegrimApiFactory();
        await factory.CreateUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SetupService>().MarkExistingInstallCompleteAsync();
        }
        Assert.True((await factory.CreateClient().GetFromJsonAsync<SetupStatusDto>("/api/setup"))!.SetupComplete);

        using var midWizard = new FossegrimApiFactory();
        (await midWizard.CreateClient().PostAsJsonAsync("/api/setup/admin", FirstAdmin)).EnsureSuccessStatusCode();
        using (var scope = midWizard.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SetupService>().MarkExistingInstallCompleteAsync();
        }
        Assert.False((await midWizard.CreateClient().GetFromJsonAsync<SetupStatusDto>("/api/setup"))!.SetupComplete);
    }

    [Fact]
    public async Task Registration_IsClosedUntilAnAdminOpensIt()
    {
        using var factory = new FossegrimApiFactory();
        var admin = await factory.CreateAdminAsync();
        var user = await factory.CreateUserClientAsync();
        var anonymous = factory.CreateClient();
        var request = new RegisterRequest("newcomer@example.com", "Newcomer", "Passw0rd!x");

        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync("/api/auth/register", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync("/api/admin/registration", new RegistrationSettingsRequest(true))).StatusCode);

        var opened = await admin.Client.PutAsJsonAsync("/api/admin/registration", new RegistrationSettingsRequest(true));
        Assert.True((await opened.Content.ReadFromJsonAsync<SetupStatusDto>())!.RegistrationOpen);
        (await anonymous.PostAsJsonAsync("/api/auth/register", request)).EnsureSuccessStatusCode();

        await admin.Client.PutAsJsonAsync("/api/admin/registration", new RegistrationSettingsRequest(false));
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync("/api/auth/register", request with { Email = "late@example.com" })).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admin_CanCreateAccounts(bool isAdmin)
    {
        using var factory = new FossegrimApiFactory();
        var admin = await factory.CreateAdminAsync();

        var response = await admin.Client.PostAsJsonAsync("/api/admin/users", new CreateUserRequest("friend@example.com", "Friend", "Passw0rd!x", isAdmin));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("friend@example.com", "Passw0rd!x"));
        login.EnsureSuccessStatusCode();
        Assert.Equal(isAdmin, (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Roles.Contains("Admin"));
    }

    [Fact]
    public async Task CreatingAccounts_IsAdminOnly_AndRejectsDuplicates()
    {
        using var factory = new FossegrimApiFactory();
        var admin = await factory.CreateAdminAsync();
        var user = await factory.CreateUserClientAsync();
        var request = new CreateUserRequest("friend@example.com", "Friend", "Passw0rd!x", true);

        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/admin/users", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.Client.PostAsJsonAsync("/api/admin/users", request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PostAsJsonAsync("/api/admin/users", request)).StatusCode);
    }
}
