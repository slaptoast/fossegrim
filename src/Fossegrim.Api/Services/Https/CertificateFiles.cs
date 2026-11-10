using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fossegrim.Contracts.Dtos;

namespace Fossegrim.Api.Services.Https;

/// <summary>
/// The certificate folder shared with the proxy container (Docker mounts the
/// same volume at /certs in both). The proxy serves HTTPS whenever
/// fullchain.pem and privkey.pem are there, and reloads when they change (see
/// proxy/fossegrim-proxy.sh). Files dropped in by hand work the same way as
/// ones Let's Encrypt issued.
/// </summary>
public class CertificateFiles
{
    public const string FullChainFile = "fullchain.pem";
    public const string PrivateKeyFile = "privkey.pem";
    // Present: the proxy serves the app over plain HTTP too, instead of redirecting.
    public const string RedirectOffFile = "http-redirect-off";

    public CertificateFiles(IConfiguration configuration)
    {
        Directory = configuration["Https:CertificateDirectory"] is { Length: > 0 } directory ? directory : null;
    }

    /// <summary>Null when the server isn't running with a certificate folder.</summary>
    public string? Directory { get; }

    public bool Available => Directory is not null;

    public string FullChainPath => Path.Combine(Directory!, FullChainFile);

    public string PrivateKeyPath => Path.Combine(Directory!, PrivateKeyFile);

    /// <summary>The certificate in the folder, or null if there isn't a readable one.</summary>
    public CertificateInfoDto? Read()
    {
        if (!Available || !File.Exists(FullChainPath) || !File.Exists(PrivateKeyPath))
        {
            return null;
        }

        try
        {
            using var certificate = LoadLeaf(File.ReadAllText(FullChainPath));
            return certificate is null ? null : Describe(certificate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces the certificate. Each file is written beside its final name
    /// and then renamed over it, so the proxy never reads half a file; the
    /// key goes first, so the moment the new chain appears its key is there.
    /// </summary>
    public void Write(string fullChainPem, string privateKeyPem)
    {
        System.IO.Directory.CreateDirectory(Directory!);
        WriteReplacing(PrivateKeyPath, privateKeyPem, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        WriteReplacing(FullChainPath, fullChainPem, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    /// <summary>Removes the certificate, so the proxy goes back to plain HTTP.</summary>
    public void Delete()
    {
        if (!Available || !System.IO.Directory.Exists(Directory))
        {
            return;
        }
        // Chain first: without it the proxy stops using the key.
        File.Delete(FullChainPath);
        File.Delete(PrivateKeyPath);
    }

    public void SetRedirect(bool enabled)
    {
        if (!Available)
        {
            return;
        }

        var marker = Path.Combine(Directory!, RedirectOffFile);
        if (enabled)
        {
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
        else if (!File.Exists(marker))
        {
            System.IO.Directory.CreateDirectory(Directory!);
            File.WriteAllText(marker, "Delete this file to redirect plain HTTP to HTTPS again (or use Fossegrim's Settings page).\n");
        }
    }

    /// <summary>The paths of the files in the folder, for backups.</summary>
    public IEnumerable<string> ExistingFiles()
    {
        if (!Available || !System.IO.Directory.Exists(Directory))
        {
            return [];
        }
        return new[] { FullChainFile, PrivateKeyFile, RedirectOffFile }
            .Select(name => Path.Combine(Directory!, name))
            .Where(File.Exists);
    }

    /// <summary>The first certificate in a PEM chain (the server's own), or null if there's none.</summary>
    public static X509Certificate2? LoadLeaf(string pem)
    {
        var span = pem.AsSpan();
        while (PemEncoding.TryFind(span, out var fields))
        {
            if (span[fields.Label].SequenceEqual("CERTIFICATE"))
            {
                return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(span[fields.Base64Data].ToString()));
            }
            span = span[fields.Location.End.Value..];
        }
        return null;
    }

    public static CertificateInfoDto Describe(X509Certificate2 certificate)
    {
        var domains = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(san => san.EnumerateDnsNames())
            .ToList();
        if (domains.Count == 0 && certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false) is { Length: > 0 } commonName)
        {
            domains.Add(commonName);
        }

        // e.g. "Let's Encrypt (R11)".
        var issuer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        if (OrganizationOf(certificate.IssuerName) is { } organization && !issuer.Contains(organization))
        {
            issuer = $"{organization} ({issuer})";
        }

        return new CertificateInfoDto(
            domains,
            issuer,
            certificate.NotBefore.ToUniversalTime(),
            certificate.NotAfter.ToUniversalTime(),
            // Let's Encrypt's staging hierarchy is named "(STAGING) ...".
            certificate.IssuerName.Name.Contains("(STAGING)", StringComparison.OrdinalIgnoreCase));
    }

    private static string? OrganizationOf(X500DistinguishedName name) =>
        name.EnumerateRelativeDistinguishedNames()
            .Where(rdn => rdn.GetSingleElementType().Value == "2.5.4.10")
            .Select(rdn => rdn.GetSingleElementValue())
            .FirstOrDefault();

    private static void WriteReplacing(string path, string contents, UnixFileMode mode)
    {
        var temporary = path + ".new";
        // The mode only applies to a new file, so don't reuse one left by a crash.
        File.Delete(temporary);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = mode;
        }
        using (var stream = new FileStream(temporary, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(contents);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
