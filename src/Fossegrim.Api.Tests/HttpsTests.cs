using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Fossegrim.Api.Services;
using Fossegrim.Api.Services.Https;
using Fossegrim.Contracts.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Fossegrim.Api.Tests;

/// <summary>The API with a certificate folder, a fake Let's Encrypt and a controllable clock.</summary>
public class HttpsApiFactory : FossegrimApiFactory
{
    public HttpsApiFactory()
    {
        Acme = new FakeAcmeIssuer(Time);
    }

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-03-01T12:00:00Z"));

    public FakeAcmeIssuer Acme { get; }

    public bool WithCertificateFolder { get; init; } = true;

    public string FullChainPath => Path.Combine(CertificateDirectory, CertificateFiles.FullChainFile);

    public string PrivateKeyPath => Path.Combine(CertificateDirectory, CertificateFiles.PrivateKeyFile);

    public string RedirectOffPath => Path.Combine(CertificateDirectory, CertificateFiles.RedirectOffFile);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (WithCertificateFolder)
        {
            builder.UseSetting("Https:CertificateDirectory", CertificateDirectory);
        }
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IAcmeIssuer>(Acme);
        });
    }

    public async Task RenewIfDueAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CertificateService>().RenewIfDueAsync(CancellationToken.None);
    }
}

// Certificate settings are server-wide, so each test gets its own server.
public class HttpsTests
{
    private static SaveLetsEncryptRequest Request(params string[] domains) =>
        new(domains, "admin@example.com", Staging: false, AgreeToTerms: true);

    [Fact]
    public async Task WithoutCertificateFolder_ReportsUnavailableAndRefusesRequests()
    {
        using var factory = new HttpsApiFactory { WithCertificateFolder = false };
        var admin = await factory.CreateAdminAsync();

        var status = await admin.Client.GetFromJsonAsync<HttpsStatusDto>("/api/admin/https");
        Assert.False(status!.Available);

        var response = await admin.Client.PutAsJsonAsync("/api/admin/https/letsencrypt", Request("music.example.com"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.Acme.Orders);
    }

    [Fact]
    public async Task HttpsSettings_AreAdminOnly()
    {
        using var factory = new HttpsApiFactory();
        var user = await factory.CreateUserClientAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/https")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync("/api/admin/https/letsencrypt", Request("music.example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/admin/https")).StatusCode);
    }

    [Theory]
    [InlineData("*.example.com")]
    [InlineData("192.168.1.10")]
    [InlineData("localhost")]
    [InlineData("music.example.com:8443")]
    [InlineData("pi.local")]
    [InlineData("https://music.example.com")]
    public async Task Save_RejectsNamesLetsEncryptWontIssueFor(string domain)
    {
        using var factory = new HttpsApiFactory();
        var admin = await factory.CreateAdminAsync();

        var response = await admin.Client.PutAsJsonAsync("/api/admin/https/letsencrypt", Request(domain));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Acme.Orders);
    }

    [Fact]
    public async Task Save_RequiresAgreeingToTheTerms()
    {
        using var factory = new HttpsApiFactory();
        var admin = await factory.CreateAdminAsync();

        var response = await admin.Client.PutAsJsonAsync("/api/admin/https/letsencrypt", Request("music.example.com") with { AgreeToTerms = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>();
        Assert.Contains("Subscriber Agreement", error!.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NormalizesDomains()
    {
        var (settings, error) = CertificateService.Validate(new SaveLetsEncryptRequest(
            ["Music.Example.COM.", " music.example.com ", "bølge.example.no", ""], "  ", Staging: true, AgreeToTerms: true));

        Assert.Null(error);
        Assert.Equal(["music.example.com", "xn--blge-gra.example.no"], settings!.Domains);
        Assert.Null(settings.Email);
        Assert.True(settings.Staging);
    }

    [Fact]
    public async Task Save_GetsCertificateAndWritesItForTheProxy()
    {
        using var factory = new HttpsApiFactory();
        var admin = await factory.CreateAdminAsync();

        var started = await admin.Client.PutAsJsonAsync("/api/admin/https/letsencrypt", Request("music.example.com", "www.music.example.com"));
        started.EnsureSuccessStatusCode();
        var status = await WaitUntilIdleAsync(admin.Client);

        var order = Assert.Single(factory.Acme.Orders);
        Assert.Equal(["music.example.com", "www.music.example.com"], order.Domains);
        Assert.Equal("admin@example.com", order.Email);
        Assert.False(order.Staging);
        Assert.Equal("fake-account-1", order.AccountKeyPem);

        Assert.True(File.Exists(factory.FullChainPath));
        Assert.True(File.Exists(factory.PrivateKeyPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(factory.PrivateKeyPath));
        }

        Assert.Null(status.LastError);
        Assert.Equal(factory.Time.GetUtcNow().UtcDateTime, status.LastSuccessAt);
        Assert.Equal(["music.example.com", "www.music.example.com"], status.Certificate!.Domains);
        Assert.False(status.Certificate.Staging);
        Assert.Contains("Fake Encrypt", status.Certificate.Issuer);
        Assert.True(status.RedirectHttp);
        Assert.False(File.Exists(factory.RedirectOffPath));
        // Renews with a third of the 90 days left.
        Assert.Equal(status.Certificate.NotBefore.AddDays(60), status.NextAttemptAt);
    }

    [Fact]
    public async Task Status_ShowsRequestInProgress()
    {
        using var factory = new HttpsApiFactory();
        var admin = await factory.CreateAdminAsync();
        factory.Acme.Gate = new TaskCompletionSource();

        var started = await (await admin.Client.PutAsJsonAsync("/api/admin/https/letsencrypt", Request("music.example.com"))).Content.ReadFromJsonAsync<HttpsStatusDto>();
        Assert.True(started!.Running);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Client.PostAsync("/api/admin/https/letsencrypt/renew", null)).StatusCode);

        factory.Acme.Gate.SetResult();
        var status = await WaitUntilIdleAsync(admin.Client);
        Assert.NotNull(status.Certificate);
    }

    [Fact]
    public async Task ChallengeAnswers_AreServedAnonymously()
    {
        using var factory = new HttpsApiFactory();
        factory.Services.GetRequiredService<AcmeChallenges>().Add("token-123", "token-123.thumbprint");
        var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/acme-challenge/token-123");
        response.EnsureSuccessStatusCode();
        Assert.Equal("token-123.thumbprint", await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/.well-known/acme-challenge/other")).StatusCode);
    }

    [Fact]
    public async Task FailedRequest_IsReportedAndRetriedLaterWithBackoff()
    {
        using var factory = new HttpsApiFactory();
        var admin = await factory.CreateAdminAsync();
        factory.Acme.Error = "Let's Encrypt couldn't check music.example.com: connection refused.";

        await admin.Client.PutAsJsonAsync("/api/admin/https/letsencrypt", Request("music.example.com"));
        var status = await WaitUntilIdleAsync(admin.Client);

        Assert.Null(status.Certificate);
        Assert.Equal(factory.Acme.Error, status.LastError);
        var firstAttempt = factory.Time.GetUtcNow().UtcDateTime;
        Assert.Equal(firstAttempt.AddHours(1), status.NextAttemptAt);

        // Not yet due: the worker leaves it alone.
        factory.Time.Advance(TimeSpan.FromMinutes(59));
        await factory.RenewIfDueAsync();
        Assert.Single(factory.Acme.Orders);

        // Due: tries again with the same account, fails again, and waits twice as long.
        factory.Time.Advance(TimeSpan.FromMinutes(1));
        await factory.RenewIfDueAsync();
        Assert.Equal(2, factory.Acme.Orders.Count);
        Assert.Equal(factory.Acme.Orders[0].AccountKeyPem, factory.Acme.Orders[1].AccountKeyPem);
        status = (await admin.Client.GetFromJsonAsync<HttpsStatusDto>("/api/admin/https"))!;
        Assert.Equal(factory.Time.GetUtcNow().UtcDateTime.AddHours(2), status.NextAttemptAt);

        // Fixed: the next attempt succeeds and clears the error.
        factory.Acme.Error = null;
        factory.Time.Advance(TimeSpan.FromHours(2));
        await factory.RenewIfDueAsync();
        status = (await admin.Client.GetFromJsonAsync<HttpsStatusDto>("/api/admin/https"))!;
        Assert.Null(status.LastError);
        Assert.NotNull(status.Certificate);
    }

    [Fact]
    public async Task Worker_RenewsWithAThirdOfTheLifetimeLeftAndReusesTheAccount()
    {
        using var factory = new HttpsApiFactory();
        var admin = await factory.CreateAdminAsync();
        await admin.Client.PutAsJsonAsync("/api/admin/https/letsencrypt", Request("music.example.com"));
        await WaitUntilIdleAsync(admin.Client);

        factory.Time.Advance(TimeSpan.FromDays(59));
        await factory.RenewIfDueAsync();
        Assert.Single(factory.Acme.Orders);

        factory.Time.Advance(TimeSpan.FromDays(1));
        await factory.RenewIfDueAsync();
        Assert.Equal(2, factory.Acme.Orders.Count);
        Assert.Equal(factory.Acme.Orders[0].AccountKeyPem, factory.Acme.Orders[1].AccountKeyPem);

        var status = (await admin.Client.GetFromJsonAsync<HttpsStatusDto>("/api/admin/https"))!;
        Assert.Equal(factory.Time.GetUtcNow().UtcDateTime, status.Certificate!.NotBefore);
    }

    [Fact]
    public async Task Worker_RequestsAgainWhenTheCertificateNoLongerMatchesTheSettings()
    {
        using var factory = new HttpsApiFactory();
        var admin = await factory.CreateAdminAsync();
        await admin.Client.PutAsJsonAsync("/api/admin/https/letsencrypt", Request("music.example.com") with { Staging = true });
        var status = await WaitUntilIdleAsync(admin.Client);
        Assert.True(status.Certificate!.Staging);

        // Switching from test mode to a real certificate gets one straight away.
        await admin.Client.PutAsJsonAsync("/api/admin/https/letsencrypt", Request("music.example.com"));
        status = await WaitUntilIdleAsync(admin.Client);
        Assert.False(status.Certificate!.Staging);
        // Staging and production have separate accounts.
        Assert.NotEqual(factory.Acme.Orders[0].AccountKeyPem, factory.Acme.Orders[1].AccountKeyPem);

        await factory.RenewIfDueAsync();
        Assert.Equal(2, factory.Acme.Orders.Count);
    }

    [Fact]
    public async Task HandPlacedCertificate_IsShownButNotRenewed()
    {
        using var factory = new HttpsApiFactory();
        var admin = await factory.CreateAdminAsync();
        Directory.CreateDirectory(factory.CertificateDirectory);
        var (chain, key) = FakeAcmeIssuer.CreateCertificate(["own.example.com"], staging: false, factory.Time.GetUtcNow().AddDays(-80), TimeSpan.FromDays(90));
        await File.WriteAllTextAsync(factory.FullChainPath, chain);
        await File.WriteAllTextAsync(factory.PrivateKeyPath, key);

        var status = (await admin.Client.GetFromJsonAsync<HttpsStatusDto>("/api/admin/https"))!;
        Assert.Equal(["own.example.com"], status.Certificate!.Domains);
        Assert.Null(status.LetsEncrypt);
        Assert.Null(status.NextAttemptAt);

        await factory.RenewIfDueAsync();
        Assert.Empty(factory.Acme.Orders);
    }

    [Fact]
    public async Task Redirect_CanBeTurnedOffForTheProxy()
    {
        using var factory = new HttpsApiFactory();
        var admin = await factory.CreateAdminAsync();

        var status = await (await admin.Client.PutAsJsonAsync("/api/admin/https/redirect", new HttpsRedirectRequest(false))).Content.ReadFromJsonAsync<HttpsStatusDto>();
        Assert.False(status!.RedirectHttp);
        Assert.True(File.Exists(factory.RedirectOffPath));

        status = await (await admin.Client.PutAsJsonAsync("/api/admin/https/redirect", new HttpsRedirectRequest(true))).Content.ReadFromJsonAsync<HttpsStatusDto>();
        Assert.True(status!.RedirectHttp);
        Assert.False(File.Exists(factory.RedirectOffPath));
    }

    [Fact]
    public async Task TurnOff_StopsRenewingAndRemovesTheCertificate()
    {
        using var factory = new HttpsApiFactory();
        var admin = await factory.CreateAdminAsync();
        await admin.Client.PutAsJsonAsync("/api/admin/https/letsencrypt", Request("music.example.com"));
        await WaitUntilIdleAsync(admin.Client);

        var response = await admin.Client.DeleteAsync("/api/admin/https/letsencrypt");
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<HttpsStatusDto>();

        Assert.Null(status!.LetsEncrypt);
        Assert.Null(status.Certificate);
        Assert.False(File.Exists(factory.FullChainPath));
        Assert.False(File.Exists(factory.PrivateKeyPath));

        factory.Time.Advance(TimeSpan.FromDays(1));
        await factory.RenewIfDueAsync();
        Assert.Single(factory.Acme.Orders);
    }

    [Fact]
    public async Task Backups_IncludeTheCertificate()
    {
        using var factory = new HttpsApiFactory();
        var admin = await factory.CreateAdminAsync();
        await admin.Client.PutAsJsonAsync("/api/admin/https/letsencrypt", Request("music.example.com"));
        await WaitUntilIdleAsync(admin.Client);

        string path;
        using (var scope = factory.Services.CreateScope())
        {
            var backups = scope.ServiceProvider.GetRequiredService<BackupService>();
            var run = await backups.RunAsync(BackupService.ManualTrigger);
            Assert.True(run!.Success, run.Error);
            path = Path.Combine(backups.Directory, run.FileName!);
        }

        using var zip = ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("certs/fullchain.pem"));
        Assert.NotNull(zip.GetEntry("certs/privkey.pem"));
    }

    private static async Task<HttpsStatusDto> WaitUntilIdleAsync(HttpClient client)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = (await client.GetFromJsonAsync<HttpsStatusDto>("/api/admin/https"))!;
            if (!status.Running)
            {
                return status;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("The certificate request didn't finish.");
    }
}
