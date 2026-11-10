using System.Security.Claims;
using Fossegrim.Api.Services;
using Fossegrim.Contracts.Dtos;
using Fossegrim.Lib.Data;
using Fossegrim.Lib.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fossegrim.Api.Endpoints;

/// <summary>
/// "Forgot password?" (an emailed link, when an admin has set up email),
/// resetting a password from a link, the admin's list of accounts with
/// "Reset link", deleting and restoring accounts, and the outgoing mail
/// server settings.
/// </summary>
public static class PasswordReset
{
    public static void MapPasswordResetEndpoints(this WebApplication app)
    {
        var authGroup = app.MapGroup("/api/auth").WithTags("Auth");

        authGroup.MapGet("/password-reset", async (EmailSettingsStore email) =>
            Results.Ok(new PasswordResetOptionsDto(await email.IsConfiguredAsync())))
        .WithName("GetPasswordResetOptions");

        // Always 202, whether or not the address has an account.
        authGroup.MapPost("/forgot-password", async (ForgotPasswordRequest request, EmailSettingsStore email, PasswordResets resets) =>
        {
            if (await email.GetAsync() is not { } settings)
            {
                return Results.Problem("This server can't send email. Ask an admin for a reset link.", statusCode: StatusCodes.Status409Conflict);
            }
            await resets.RequestByEmailAsync(request.Email, settings);
            return Results.Accepted();
        })
        .RequireRateLimiting(SignInLimits.Policy)
        .WithName("ForgotPassword");

        authGroup.MapPost("/reset-password", async (ResetPasswordRequest request, PasswordResets resets) =>
            await resets.ResetAsync(request) is { } errors
                ? Results.BadRequest(new IdentityErrorsResponse(errors.ToList()))
                : Results.NoContent())
        .RequireRateLimiting(SignInLimits.Policy)
        .WithName("ResetPassword");

        var adminGroup = app.MapGroup("/api/admin")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole("Admin"));

        // The accounts, or with ?deleted=true the deleted ones (to restore).
        adminGroup.MapGet("/users", async (bool? deleted, FossegrimDbContext db) =>
        {
            var adminIds = await AccountDeletion.AdminIdsAsync(db);
            var now = DateTimeOffset.UtcNow;
            var users = await db.Users.AsNoTracking()
                .Where(u => deleted == true ? u.DeletedAt != null : u.DeletedAt == null)
                .ToListAsync();
            return Results.Ok(users
                .Select(u => new AdminUserDto(u.Id, u.Email ?? "", u.DisplayName ?? "", adminIds.Contains(u.Id), u.LockoutEnd > now, u.DeletedAt))
                .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList());
        })
        .WithName("GetUsers");

        // For an admin to pass on to someone who can't get the email.
        adminGroup.MapPost("/users/{id}/reset-link", async (string id, UserManager<ApplicationUser> users, PasswordResets resets) =>
            await users.FindByIdAsync(id) is { DeletedAt: null } user
                ? Results.Ok(await resets.CreateLinkAsync(user))
                : Results.NotFound())
        .WithName("CreatePasswordResetLink");

        adminGroup.MapDelete("/users/{id}", async (string id, ClaimsPrincipal principal, AccountDeletion deletion) =>
            await deletion.DeleteAsync(id, principal) switch
            {
                AccountDeletion.Result.Done => Results.NoContent(),
                AccountDeletion.Result.NotFound => Results.NotFound(),
                AccountDeletion.Result.Self => Results.Conflict(new ApiErrorResponse("You can't delete your own account.")),
                _ => Results.Conflict(new ApiErrorResponse("That's the only admin account. Make someone else an admin first.")),
            })
        .WithName("DeleteUser");

        adminGroup.MapPost("/users/{id}/restore", async (string id, AccountDeletion deletion) =>
            await deletion.RestoreAsync(id) ? Results.NoContent() : Results.NotFound())
        .WithName("RestoreUser");

        adminGroup.MapGet("/email", async (EmailSettingsStore email) => Results.Ok(await email.GetDtoAsync()))
            .WithName("GetEmailSettings");

        adminGroup.MapPut("/email", async (SaveEmailSettingsRequest request, EmailSettingsStore email) =>
        {
            var settings = await email.ResolveAsync(request);
            if (EmailSettingsStore.Validate(settings) is { } error)
            {
                return Results.BadRequest(new ApiErrorResponse(error));
            }
            await email.SaveAsync(settings);
            return Results.Ok(await email.GetDtoAsync());
        })
        .WithName("SaveEmailSettings");

        adminGroup.MapDelete("/email", async (EmailSettingsStore email) =>
        {
            await email.ClearAsync();
            return Results.NoContent();
        })
        .WithName("ClearEmailSettings");

        // Sends straight away (not through the queue) so the admin sees what went wrong.
        adminGroup.MapPost("/email/test", async (TestEmailRequest request, EmailSettingsStore email, IMailTransport transport, CancellationToken cancellationToken) =>
        {
            if (await email.GetAsync() is not { } settings)
            {
                return Results.BadRequest(new ApiErrorResponse("Save the mail server settings first."));
            }
            if (!MimeKit.MailboxAddress.TryParse(request.To, out var to) || !to.Address.Contains('@'))
            {
                return Results.BadRequest(new ApiErrorResponse("Enter an address to send the test email to."));
            }

            try
            {
                await transport.SendAsync(settings, new OutgoingEmail(to.Address, "Fossegrim test email",
                    $"This is a test email from Fossegrim at {settings.PublicUrl}. Email is working, so \"Forgot password?\" can send reset links."),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.BadRequest(new ApiErrorResponse($"Sending failed: {ex.Message}"));
            }
            return Results.NoContent();
        })
        .WithName("SendTestEmail");
    }
}
