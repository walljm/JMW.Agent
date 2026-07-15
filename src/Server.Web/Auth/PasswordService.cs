using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace JMW.Discovery.Server.Auth;

/// <summary>
/// PBKDF2-based password hashing. New hashes use 600k iterations, SHA-256, 32-byte salt + 32-byte
/// hash, stored as "pbkdf2-sha256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;" so the
/// work factor can be raised again later without invalidating existing accounts. Verify() still
/// accepts the legacy "base64(salt):base64(hash)" format (implicitly 100k iterations) so hashes
/// stored before this change keep working; NeedsRehash() tells callers when to re-hash (with the
/// just-verified password) on the caller's next successful login.
/// </summary>
public sealed class PasswordService
{
    private const int Iterations = 600_000;
    private const int LegacyIterations = 100_000;
    private const int SaltLength = 32;
    private const int HashLength = 32;
    private const string Prefix = "pbkdf2-sha256";

    public string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] hash = Pbkdf2(password, salt, Iterations);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string storedHash)
    {
        if (!TryParse(storedHash, out int iterations, out byte[]? salt, out byte[]? expected))
        {
            return false;
        }

        byte[] actual = Pbkdf2(password, salt, iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// True if the stored hash is in the legacy format or below the current iteration count and
    /// should be replaced with <see cref="Hash" /> the next time this password is verified.
    /// </summary>
    public bool NeedsRehash(string storedHash) =>
        !TryParse(storedHash, out int iterations, out _, out _) || iterations < Iterations;

    private static bool TryParse(
        string storedHash,
        out int iterations,
        [NotNullWhen(true)] out byte[]? salt,
        [NotNullWhen(true)] out byte[]? hash
    )
    {
        iterations = 0;
        salt = null;
        hash = null;

        if (storedHash.StartsWith(Prefix + "$", StringComparison.Ordinal))
        {
            string[] parts = storedHash.Split('$');
            if (parts.Length != 4 || !int.TryParse(parts[1], out iterations) || iterations <= 0)
            {
                iterations = 0;
                return false;
            }

            return TryDecode(parts[2], parts[3], out salt, out hash);
        }

        // Legacy format: base64(salt):base64(hash), implicitly 100k iterations.
        string[] legacyParts = storedHash.Split(':', 2);
        if (legacyParts.Length != 2)
        {
            return false;
        }

        iterations = LegacyIterations;
        return TryDecode(legacyParts[0], legacyParts[1], out salt, out hash);
    }

    private static bool TryDecode(
        string saltB64,
        string hashB64,
        [NotNullWhen(true)] out byte[]? salt,
        [NotNullWhen(true)] out byte[]? hash
    )
    {
        try
        {
            salt = Convert.FromBase64String(saltB64);
            hash = Convert.FromBase64String(hashB64);
            return true;
        }
        catch (FormatException)
        {
            salt = null;
            hash = null;
            return false;
        }
    }

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashLength
        );
}