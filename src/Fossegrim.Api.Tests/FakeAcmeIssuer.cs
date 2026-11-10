using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fossegrim.Api.Services.Https;

namespace Fossegrim.Api.Tests;

/// <summary>
/// Stands in for Let's Encrypt: issues a self-signed certificate for the
/// order's domains, valid for Lifetime from the given clock's now, or fails
/// with Error when it's set.
/// </summary>
public class FakeAcmeIssuer(TimeProvider time) : IAcmeIssuer
{
    private int _accounts;

    public List<AcmeOrder> Orders { get; } = [];

    public string NewAccountKeyPem() => $"fake-account-{Interlocked.Increment(ref _accounts)}";

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(90);

    public string? Error { get; set; }

    // Lets a test look at the server while a request is in progress.
    public TaskCompletionSource? Gate { get; set; }

    public async Task<AcmeCertificate> IssueAsync(AcmeOrder order, CancellationToken cancellationToken)
    {
        lock (Orders)
        {
            Orders.Add(order);
        }
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }
        if (Error is { } error)
        {
            throw new AcmeException(error);
        }

        var (chain, key) = CreateCertificate(order.Domains, order.Staging, time.GetUtcNow(), Lifetime);
        return new AcmeCertificate(chain, key);
    }

    public static (string ChainPem, string KeyPem) CreateCertificate(IReadOnlyList<string> domains, bool staging, DateTimeOffset notBefore, TimeSpan lifetime)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = new X500DistinguishedName(staging ? "CN=(STAGING) Fake Issuer, O=Fake Encrypt" : "CN=Fake Issuer, O=Fake Encrypt");
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var domain in domains)
        {
            san.AddDnsName(domain);
        }
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(notBefore, notBefore + lifetime);
        return (certificate.ExportCertificatePem() + "\n", key.ExportPkcs8PrivateKeyPem() + "\n");
    }
}
