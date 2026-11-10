namespace Fossegrim.Contracts.Dtos;

// The HTTPS card on the Settings page. Available is false when the server
// has no certificate folder (Https:CertificateDirectory), i.e. it isn't
// running behind the bundled proxy, so there's nothing to manage here.
public record HttpsStatusDto(
    bool Available,
    // The certificate the proxy serves, whether Let's Encrypt issued it or
    // someone put it in the folder by hand. Null when there isn't one.
    CertificateInfoDto? Certificate,
    // Null when Let's Encrypt isn't set up.
    LetsEncryptSettingsDto? LetsEncrypt,
    // Plain HTTP redirects to HTTPS while a certificate is in place.
    bool RedirectHttp,
    // A certificate request is in progress.
    bool Running,
    DateTime? LastAttemptAt,
    DateTime? LastSuccessAt,
    // Why the last request failed; null after a success.
    string? LastError,
    // When the next automatic attempt is due (a renewal or a retry).
    DateTime? NextAttemptAt);

public record CertificateInfoDto(
    IReadOnlyList<string> Domains,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    // Issued by Let's Encrypt's staging (test) service, so browsers won't trust it.
    bool Staging);

public record LetsEncryptSettingsDto(IReadOnlyList<string> Domains, string? Email, bool Staging);

// AgreeToTerms: the admin accepted Let's Encrypt's subscriber agreement.
public record SaveLetsEncryptRequest(IReadOnlyList<string> Domains, string? Email, bool Staging, bool AgreeToTerms);

public record HttpsRedirectRequest(bool Enabled);
