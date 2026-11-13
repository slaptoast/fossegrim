using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;

namespace Fossegrim.Api.Services;

/// <summary>
/// <c>fossegrim reset-password &lt;email&gt;</c>, run inside the container
/// (<c>docker exec</c>): prints a one-time reset link for the account, for an
/// admin who has forgotten their password on a server that can't send email.
/// Uses the same database and keys as the running server, so the server
/// accepts the link.
/// </summary>
public static class ResetPasswordCommand
{
    public static Task<int> RunAsync(WebApplication app, string[] args) =>
        RunAsync(app.Services, args, Console.Out, Console.Error);

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextWriter output, TextWriter error)
    {
        if (args is not [var email])
        {
            await error.WriteLineAsync("Usage: fossegrim reset-password <email>");
            return 2;
        }

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByEmailAsync(email) is not { } user)
        {
            await error.WriteLineAsync($"No account uses {email}.");
            return 1;
        }

        var link = await scope.ServiceProvider.GetRequiredService<PasswordResets>().CreateLinkAsync(user);
        var publicUrl = (await scope.ServiceProvider.GetRequiredService<EmailSettingsStore>().GetAsync())?.PublicUrl;

        await output.WriteLineAsync($"Reset link for {user.UserName} ({user.Email}), valid once for {PasswordResets.LinkLifetime.TotalHours:0} hours:");
        await output.WriteLineAsync();
        await output.WriteLineAsync(publicUrl is null ? $"  <your Fossegrim address>{link.Path}" : $"  {publicUrl}{link.Path}");
        if (publicUrl is null)
        {
            await output.WriteLineAsync();
            await output.WriteLineAsync("Put the address you open Fossegrim on in front, e.g. http://192.168.1.10:8080/reset-password?...");
        }
        return 0;
    }
}
