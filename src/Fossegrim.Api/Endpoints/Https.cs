using Fossegrim.Api.Services.Https;
using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Api.Endpoints;

/// <summary>
/// HTTPS: the admin's Let's Encrypt settings and certificate status, and the
/// public HTTP-01 challenge path Let's Encrypt fetches while checking a domain.
/// </summary>
public static class HttpsEndpoints
{
    public static void MapHttpsEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous by design: Let's Encrypt fetches it over plain HTTP on port 80.
        app.MapGet("/.well-known/acme-challenge/{token}", (string token, AcmeChallenges challenges) =>
                challenges.Find(token) is { } answer ? Results.Text(answer, "application/octet-stream") : Results.NotFound())
            .ExcludeFromDescription();

        var group = app.MapGroup("/api/admin/https")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole("Admin"));

        group.MapGet("", async (CertificateService certificates) => Results.Ok(await certificates.GetStatusAsync()))
            .WithName("GetHttpsStatus");

        // Saves the settings and requests a certificate straight away; the
        // request runs in the background and the page polls the status.
        group.MapPut("/letsencrypt", async (SaveLetsEncryptRequest request, CertificateService certificates, CertificateCoordinator coordinator,
            IServiceScopeFactory scopes, IHostApplicationLifetime lifetime) =>
        {
            if (!certificates.Available)
            {
                return NotAvailable();
            }
            var (settings, error) = CertificateService.Validate(request);
            if (error is not null)
            {
                return Results.BadRequest(new ApiErrorResponse(error));
            }
            if (!coordinator.TryStart())
            {
                return AlreadyRunning();
            }

            try
            {
                await certificates.SaveSettingsAsync(settings!);
            }
            catch
            {
                coordinator.Finished();
                throw;
            }
            StartIssuing(scopes, coordinator, lifetime.ApplicationStopping);
            return Results.Ok(await certificates.GetStatusAsync());
        })
        .WithName("SaveLetsEncryptSettings");

        group.MapPost("/letsencrypt/renew", async (CertificateService certificates, CertificateCoordinator coordinator,
            IServiceScopeFactory scopes, IHostApplicationLifetime lifetime) =>
        {
            if (!certificates.Available)
            {
                return NotAvailable();
            }
            if (await certificates.GetSettingsAsync() is null)
            {
                return Results.Conflict(new ApiErrorResponse("Set up Let's Encrypt first."));
            }
            if (!coordinator.TryStart())
            {
                return AlreadyRunning();
            }
            StartIssuing(scopes, coordinator, lifetime.ApplicationStopping);
            return Results.Ok(await certificates.GetStatusAsync());
        })
        .WithName("RenewCertificate");

        group.MapDelete("/letsencrypt", async (CertificateService certificates, CertificateCoordinator coordinator) =>
        {
            if (!coordinator.TryStart())
            {
                return AlreadyRunning();
            }
            try
            {
                await certificates.TurnOffAsync();
            }
            finally
            {
                coordinator.Finished();
            }
            return Results.Ok(await certificates.GetStatusAsync());
        })
        .WithName("TurnOffLetsEncrypt");

        group.MapPut("/redirect", async (HttpsRedirectRequest request, CertificateService certificates) =>
        {
            if (!certificates.Available)
            {
                return NotAvailable();
            }
            if (await certificates.SetRedirectAsync(request.Enabled) is { } error)
            {
                return Results.Conflict(new ApiErrorResponse(error));
            }
            return Results.Ok(await certificates.GetStatusAsync());
        })
        .WithName("SetHttpsRedirect");
    }

    // The coordinator is already held; this releases it when the request is done.
    private static void StartIssuing(IServiceScopeFactory scopes, CertificateCoordinator coordinator, CancellationToken stopping) =>
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<CertificateService>().IssueAsync(stopping);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            finally
            {
                coordinator.Finished();
            }
        }, CancellationToken.None);

    private static IResult NotAvailable() => Results.Conflict(new ApiErrorResponse(
        "This server has no certificate folder, so it can't serve HTTPS itself. Run it with the bundled proxy (see DOCKER.md)."));

    private static IResult AlreadyRunning() => Results.Conflict(new ApiErrorResponse("A certificate request is already in progress."));
}
