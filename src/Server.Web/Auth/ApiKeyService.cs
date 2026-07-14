using System.Security.Cryptography;
using System.Text;

namespace JMW.Discovery.Server.Agents;

public sealed class ApiKeyService
{
    // The value shipped in docker-compose.yml before an operator overrides it via .env; anyone who
    // has read this repo knows it, so it must never be trusted as real keying material.
    private const string PublishedExampleSecret = "dev-api-key-secret-change-in-production";
    private const int MinimumSecretBytes = 32;

    private readonly byte[] _secretKey;

    public ApiKeyService()
    {
        string secret = Environment.GetEnvironmentVariable("JMW_API_KEY_SECRET")
         ?? throw new InvalidOperationException("JMW_API_KEY_SECRET environment variable is required.");

        if (secret == PublishedExampleSecret)
        {
            throw new InvalidOperationException(
                "JMW_API_KEY_SECRET is still set to the published example value from docker-compose.yml. "
              + "Generate a real secret (e.g. `openssl rand -hex 32`) and set it in your .env file."
            );
        }

        if (Encoding.UTF8.GetByteCount(secret) < MinimumSecretBytes)
        {
            throw new InvalidOperationException(
                $"JMW_API_KEY_SECRET must be at least {MinimumSecretBytes} bytes. "
              + "Generate one with `openssl rand -hex 32`."
            );
        }

        _secretKey = Encoding.UTF8.GetBytes(secret);
    }

    public string Generate() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public string Hash(string plaintext) =>
        Convert.ToHexString(
                HMACSHA256.HashData(_secretKey, Encoding.UTF8.GetBytes(plaintext))
            )
            .ToLowerInvariant();

    public bool Verify(string plaintext, string storedHash)
    {
        byte[] actual = Encoding.UTF8.GetBytes(Hash(plaintext));
        byte[] expected = Encoding.UTF8.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}