using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Fossegrim.Api.Tests;

/// <summary>"Forgot password?" with email set up, reset links, and the email settings.</summary>
public partial class PasswordResetTests : IClassFixture<FossegrimApiFactory>
{
    private const string PublicUrl = "https://music.example.com";
    private readonly FossegrimApiFactory _factory;

    public PasswordResetTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ForgotPassword_EmailsALink_ThatSetsANewPassword_AndSignsOutEverywhere()
    {
        await SetUpEmailAsync();
        var user = await _factory.CreateUserAsync();
        var anonymous = _factory.CreateClient();

        Assert.True((await anonymous.GetFromJsonAsync<PasswordResetOptionsDto>("/api/auth/password-reset"))!.ByEmail);
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(user.Email))).StatusCode);

        var email = await _factory.Mail.WaitForEmailToAsync(user.Email);
        var (userId, token) = ParseLink(email.Body, PublicUrl);
        Assert.Equal(user.UserId, userId);

        var reset = await anonymous.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(userId, token, "N3w-passw0rd!"));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, user.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, "N3w-passw0rd!"))).StatusCode);
    }

    [Fact]
    public async Task ResetLinks_WorkOnce()
    {
        var user = await _factory.CreateUserAsync();
        var (userId, token) = await AdminLinkAsync(user.UserId);
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(userId, token, "First-passw0rd!"))).StatusCode);

        var again = await anonymous.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(userId, token, "Second-passw0rd!"));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal([PasswordResets.InvalidLinkMessage], (await again.Content.ReadFromJsonAsync<IdentityErrorsResponse>())!.Errors);
    }

    [Theory]
    [InlineData("not-base64-at-all!!")]
    [InlineData("dGFtcGVyZWQ")]
    public async Task BadTokens_AreRefused(string token)
    {
        var user = await _factory.CreateUserAsync();

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(user.UserId, token, "N3w-passw0rd!"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([PasswordResets.InvalidLinkMessage], (await response.Content.ReadFromJsonAsync<IdentityErrorsResponse>())!.Errors);
    }

    [Fact]
    public async Task WeakNewPasswords_GetThePasswordRules_AndLeaveTheLinkUsable()
    {
        var user = await _factory.CreateUserAsync();
        var (userId, token) = await AdminLinkAsync(user.UserId);
        var anonymous = _factory.CreateClient();

        var weak = await anonymous.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(userId, token, "abc"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.DoesNotContain(PasswordResets.InvalidLinkMessage, (await weak.Content.ReadFromJsonAsync<IdentityErrorsResponse>())!.Errors);

        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(userId, token, "N3w-passw0rd!"))).StatusCode);
    }

    [Fact]
    public async Task ForgotPassword_SaysTheSameForUnknownAddresses_AndSendsNothing()
    {
        await SetUpEmailAsync();
        var address = $"nobody-{Guid.NewGuid():N}@example.com";

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(address));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await Task.Delay(200);
        Assert.Empty(_factory.Mail.SentTo(address));
    }

    [Fact]
    public async Task ForgotPassword_SendsOneEmailPerAccountEveryFewMinutes()
    {
        await SetUpEmailAsync();
        var user = await _factory.CreateUserAsync();
        var anonymous = _factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(user.Email))).StatusCode);
        }

        await _factory.Mail.WaitForEmailToAsync(user.Email);
        await Task.Delay(200);
        Assert.Single(_factory.Mail.SentTo(user.Email));
    }

    [Fact]
    public async Task Resetting_UnlocksALockedAccount()
    {
        var user = await _factory.CreateUserAsync();
        var anonymous = _factory.CreateClient();
        for (var i = 0; i < SignInLimits.MaxFailedAttempts; i++)
        {
            await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, "wrong"));
        }

        var admin = await _factory.CreateAdminAsync();
        var users = await admin.Client.GetFromJsonAsync<List<AdminUserDto>>("/api/admin/users");
        Assert.True(users!.Single(u => u.UserId == user.UserId).LockedOut);
        Assert.True(users!.Single(u => u.UserId == admin.UserId).IsAdmin);

        var (userId, token) = await AdminLinkAsync(user.UserId, admin);
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(userId, token, "N3w-passw0rd!"))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, "N3w-passw0rd!"))).StatusCode);
    }

    [Fact]
    public async Task OnlyAdmins_SeeAccounts_MakeLinks_OrChangeEmailSettings()
    {
        var user = await _factory.CreateUserAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.PostAsync($"/api/admin/users/{user.UserId}/reset-link", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync("/api/admin/email")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.PutAsJsonAsync("/api/admin/email", Settings())).StatusCode);
    }

    [Fact]
    public async Task EmailSettings_KeepThePasswordToThemselves_AndKeepItWhenLeftBlank()
    {
        var admin = await _factory.CreateAdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PutAsJsonAsync("/api/admin/email", Settings(password: "smtp-secret"))).StatusCode);

        var response = await admin.Client.PutAsJsonAsync("/api/admin/email", Settings(password: null) with { FromName = "Music" });
        var saved = (await response.Content.ReadFromJsonAsync<EmailSettingsDto>())!;

        Assert.True(saved.Configured);
        Assert.True(saved.HasPassword);
        Assert.Equal("Music", saved.FromName);
        Assert.DoesNotContain("smtp-secret", await admin.Client.GetStringAsync("/api/admin/email"));

        await _factory.WithDbAsync(async db =>
            Assert.DoesNotContain("smtp-secret", (await db.AppSettings.FindAsync("Email:Smtp"))!.Value!));

        using var scope = _factory.Services.CreateScope();
        Assert.Equal("smtp-secret", (await scope.ServiceProvider.GetRequiredService<EmailSettingsStore>().GetAsync())!.Password);
    }

    [Theory]
    [InlineData("", "fossegrim@example.com", PublicUrl, "host name")]
    [InlineData("smtp.example.com", "not an address", PublicUrl, "sent from")]
    [InlineData("smtp.example.com", "fossegrim@example.com", "music.example.com", "https://")]
    [InlineData("smtp.example.com", "fossegrim@example.com", "javascript:alert(1)", "https://")]
    public async Task EmailSettings_AreChecked(string host, string from, string publicUrl, string expected)
    {
        var admin = await _factory.CreateAdminAsync();

        var response = await admin.Client.PutAsJsonAsync("/api/admin/email", Settings() with { Host = host, FromAddress = from, PublicUrl = publicUrl });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error);
    }

    [Fact]
    public async Task TestEmail_IsSentStraightAway_AndReportsFailures()
    {
        await SetUpEmailAsync();
        var admin = await _factory.CreateAdminAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.PostAsJsonAsync("/api/admin/email/test", new TestEmailRequest(admin.Email))).StatusCode);
        Assert.Single(_factory.Mail.SentTo(admin.Email));

        _factory.Mail.FailWith = new InvalidOperationException("535 authentication failed");
        try
        {
            var failed = await admin.Client.PostAsJsonAsync("/api/admin/email/test", new TestEmailRequest(admin.Email));
            Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
            Assert.Contains("535 authentication failed", (await failed.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Error);
        }
        finally
        {
            _factory.Mail.FailWith = null;
        }
    }

    [Fact]
    public async Task ResetPasswordCommand_PrintsAWorkingLink()
    {
        await SetUpEmailAsync();
        var user = await _factory.CreateUserAsync();
        var output = new StringWriter();

        Assert.Equal(0, await ResetPasswordCommand.RunAsync(_factory.Services, [user.Email], output, TextWriter.Null));

        var (userId, token) = ParseLink(output.ToString(), PublicUrl);
        var reset = await _factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(userId, token, "N3w-passw0rd!"));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
    }

    [Fact]
    public async Task ResetPasswordCommand_FailsForUnknownAddresses()
    {
        var error = new StringWriter();

        Assert.Equal(1, await ResetPasswordCommand.RunAsync(_factory.Services, ["nobody@example.com"], TextWriter.Null, error));
        Assert.Contains("No account uses", error.ToString());
    }

    private async Task SetUpEmailAsync()
    {
        var admin = await _factory.CreateAdminAsync();
        (await admin.Client.PutAsJsonAsync("/api/admin/email", Settings())).EnsureSuccessStatusCode();
    }

    private async Task<(string UserId, string Token)> AdminLinkAsync(string userId, TestUser? admin = null)
    {
        admin ??= await _factory.CreateAdminAsync();
        var response = await admin.Client.PostAsync($"/api/admin/users/{userId}/reset-link", null);
        response.EnsureSuccessStatusCode();
        var link = (await response.Content.ReadFromJsonAsync<PasswordResetLinkDto>())!;
        Assert.True(link.ExpiresAt > DateTime.UtcNow.AddHours(23));
        return ParseLink(link.Path, "");
    }

    private static SaveEmailSettingsRequest Settings(string? password = "smtp-password") =>
        new("smtp.example.com", 587, SmtpSecurity.StartTls, "fossegrim", password, "fossegrim@example.com", null, PublicUrl + "/");

    internal static (string UserId, string Token) ParseLink(string text, string prefix)
    {
        var match = LinkPattern().Match(text);
        Assert.True(match.Success, $"No reset link in: {text}");
        Assert.StartsWith(prefix + "/reset-password", match.Value);
        return (Uri.UnescapeDataString(match.Groups["user"].Value), match.Groups["token"].Value);
    }

    [GeneratedRegex(@"\S*/reset-password\?user=(?<user>[^&\s]+)&token=(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex LinkPattern();
}

/// <summary>"Forgot password?" on a server without email.</summary>
public class PasswordResetWithoutEmailTests : IClassFixture<FossegrimApiFactory>
{
    private readonly FossegrimApiFactory _factory;

    public PasswordResetWithoutEmailTests(FossegrimApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ForgotPassword_SaysToAskAnAdmin()
    {
        var user = await _factory.CreateUserAsync();
        var anonymous = _factory.CreateClient();

        Assert.False((await anonymous.GetFromJsonAsync<PasswordResetOptionsDto>("/api/auth/password-reset"))!.ByEmail);

        var response = await anonymous.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(user.Email));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Ask an admin", await response.Content.ReadAsStringAsync());
        Assert.Empty(_factory.Mail.SentTo(user.Email));
    }

    [Fact]
    public async Task TurningEmailOff_StopsEmailedLinks()
    {
        var admin = await _factory.CreateAdminAsync();
        (await admin.Client.PutAsJsonAsync("/api/admin/email", new SaveEmailSettingsRequest(
            "smtp.example.com", 465, SmtpSecurity.SslOnConnect, null, null, "fossegrim@example.com", null, "http://192.168.1.10:8080"))).EnsureSuccessStatusCode();
        Assert.True((await _factory.CreateClient().GetFromJsonAsync<PasswordResetOptionsDto>("/api/auth/password-reset"))!.ByEmail);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.DeleteAsync("/api/admin/email")).StatusCode);

        Assert.False((await _factory.CreateClient().GetFromJsonAsync<PasswordResetOptionsDto>("/api/auth/password-reset"))!.ByEmail);
        Assert.False((await admin.Client.GetFromJsonAsync<EmailSettingsDto>("/api/admin/email"))!.Configured);
    }
}
