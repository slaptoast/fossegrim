using System.Collections.Concurrent;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;

namespace Fossegrim.Api.Services.Https;

/// <summary>
/// HTTP-01 challenge answers waiting for Let's Encrypt to fetch them from
/// /.well-known/acme-challenge/{token} (see Endpoints/Https.cs). The proxy
/// passes that path through to the app even while it redirects everything
/// else to HTTPS.
/// </summary>
public class AcmeChallenges
{
    private readonly ConcurrentDictionary<string, string> _answers = new();

    public void Add(string token, string keyAuthorization) => _answers[token] = keyAuthorization;

    public void Remove(string token) => _answers.TryRemove(token, out _);

    public string? Find(string token) => _answers.TryGetValue(token, out var answer) ? answer : null;
}

// AccountKeyPem identifies the Let's Encrypt account (see NewAccountKeyPem).
public record AcmeOrder(IReadOnlyList<string> Domains, string? Email, bool Staging, string AccountKeyPem);

public record AcmeCertificate(string FullChainPem, string PrivateKeyPem);

/// <summary>A reason the certificate couldn't be issued, worded for the Settings page.</summary>
public class AcmeException(string message, Exception? inner = null) : Exception(message, inner);

public interface IAcmeIssuer
{
    /// <summary>
    /// A key for a new account. Saved before the first order, so failed
    /// attempts keep using one account (Let's Encrypt limits new accounts).
    /// </summary>
    string NewAccountKeyPem();

    /// <summary>Gets a certificate for the order's domains, proving control of each with an HTTP-01 challenge.</summary>
    Task<AcmeCertificate> IssueAsync(AcmeOrder order, CancellationToken cancellationToken);
}

/// <summary>Let's Encrypt (or its staging service), through Certes.</summary>
public class LetsEncryptIssuer : IAcmeIssuer
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromMinutes(2);

    private readonly AcmeChallenges _challenges;
    private readonly ILogger<LetsEncryptIssuer> _logger;

    public LetsEncryptIssuer(AcmeChallenges challenges, ILogger<LetsEncryptIssuer> logger)
    {
        _challenges = challenges;
        _logger = logger;
    }

    public static Uri DirectoryFor(bool staging) => staging ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2;

    public string NewAccountKeyPem() => KeyFactory.NewKey(KeyAlgorithm.ES256).ToPem();

    public async Task<AcmeCertificate> IssueAsync(AcmeOrder order, CancellationToken cancellationToken)
    {
        var accountKey = KeyFactory.FromPem(order.AccountKeyPem);
        var acme = new AcmeContext(DirectoryFor(order.Staging), accountKey);
        var tokens = new List<string>();
        try
        {
            // Registers the key, or finds the account it already belongs to,
            // and keeps its contact address in step with the settings.
            var account = await acme.NewAccount(Contacts(order.Email), termsOfServiceAgreed: true);
            await account.Update(Contacts(order.Email), agreeTermsOfService: true);

            var acmeOrder = await acme.NewOrder(order.Domains.ToList());
            foreach (var authorization in await acmeOrder.Authorizations())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resource = await authorization.Resource();
                if (resource.Status == AuthorizationStatus.Valid)
                {
                    // Proved recently; Let's Encrypt remembers for a while.
                    continue;
                }

                var challenge = await authorization.Http();
                _challenges.Add(challenge.Token, challenge.KeyAuthz);
                tokens.Add(challenge.Token);
                _logger.LogInformation("Asking Let's Encrypt to check {Domain}", resource.Identifier.Value);
                await challenge.Validate();
                await WaitForValidationAsync(authorization, resource.Identifier.Value, cancellationToken);
            }

            var certificateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
            var chain = await acmeOrder.Generate(new CsrInfo { CommonName = order.Domains[0] }, certificateKey);
            return new AcmeCertificate(chain.ToPem(), certificateKey.ToPem());
        }
        catch (AcmeRequestException ex)
        {
            throw new AcmeException($"Let's Encrypt refused the request: {ex.Error?.Detail ?? ex.Message}", ex);
        }
        catch (AcmeException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new AcmeException($"Couldn't reach Let's Encrypt: {ex.Message}", ex);
        }
        finally
        {
            foreach (var token in tokens)
            {
                _challenges.Remove(token);
            }
        }
    }

    private static async Task WaitForValidationAsync(IAuthorizationContext authorization, string domain, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ValidationTimeout;
        while (true)
        {
            await Task.Delay(PollInterval, cancellationToken);
            var resource = await authorization.Resource();
            switch (resource.Status)
            {
                case AuthorizationStatus.Valid:
                    return;
                case AuthorizationStatus.Pending when DateTime.UtcNow < deadline:
                    continue;
                case AuthorizationStatus.Pending:
                    throw new AcmeException($"Let's Encrypt didn't finish checking {domain} in time. Try again in a few minutes.");
                default:
                    var error = resource.Challenges?
                        .Where(c => c.Type == ChallengeTypes.Http01)
                        .Select(c => c.Error?.Detail)
                        .FirstOrDefault(detail => detail is not null);
                    throw new AcmeException(
                        $"Let's Encrypt couldn't check {domain}: {error ?? "the challenge failed"}. " +
                        $"Make sure {domain} points at this server and port 80 is reachable from the internet.");
            }
        }
    }

    private static List<string> Contacts(string? email) => email is { Length: > 0 } ? [$"mailto:{email}"] : [];
}
