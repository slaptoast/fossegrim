using System.Security.Cryptography;

namespace Fossegrim.Lib.Services;

public enum JwtKeySource
{
    Configured,
    KeyFileLoaded,
    KeyFileCreated,
    Ephemeral
}

/// <summary>
/// Resolves the JWT signing key so no secret ever needs to live in source:
/// an explicit key (env var / user-secrets) wins; otherwise a key file is
/// loaded, or generated on first run (Docker points this at the /data
/// volume so it survives restarts); otherwise a fresh in-memory key is
/// made, which invalidates all tokens on every restart (fine for local dev).
/// </summary>
public static class JwtKeyStore
{
    private const int KeySizeBytes = 64;

    public static (string Key, JwtKeySource Source) Resolve(string? configuredKey, string? keyFile)
    {
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            return (configuredKey, JwtKeySource.Configured);
        }

        if (string.IsNullOrWhiteSpace(keyFile))
        {
            return (GenerateKey(), JwtKeySource.Ephemeral);
        }

        if (File.Exists(keyFile))
        {
            var existing = File.ReadAllText(keyFile).Trim();
            if (existing.Length == 0)
            {
                throw new InvalidOperationException($"JWT key file '{keyFile}' is empty. Delete it to have a new key generated.");
            }
            return (existing, JwtKeySource.KeyFileLoaded);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(keyFile));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var key = GenerateKey();
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            // Owner-only from the moment the file exists, not chmod'd afterwards.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var writer = new StreamWriter(keyFile, options))
        {
            writer.Write(key);
        }
        return (key, JwtKeySource.KeyFileCreated);
    }

    private static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySizeBytes));
}
