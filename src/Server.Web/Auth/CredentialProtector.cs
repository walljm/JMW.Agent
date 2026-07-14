using System.Text;

using Microsoft.AspNetCore.DataProtection;

namespace JMW.Discovery.Server.Auth;

/// <summary>
/// Encrypts and decrypts credential secrets for storage as BYTEA.
/// Backed by ASP.NET Core Data Protection with an application-specific purpose
/// string. Never logs plaintext.
/// </summary>
public sealed class CredentialProtector
{
    private readonly IDataProtector _protector;

    public CredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("JMW.Discovery.Credentials");
    }

    /// <summary>Encrypts a plaintext secret into ciphertext suitable for BYTEA storage.</summary>
    public byte[] Encrypt(string plaintext)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        return _protector.Protect(plainBytes);
    }

    /// <summary>Decrypts ciphertext from BYTEA storage back into the plaintext secret.</summary>
    public string Decrypt(byte[] ciphertext)
    {
        byte[] plainBytes = _protector.Unprotect(ciphertext);
        return Encoding.UTF8.GetString(plainBytes);
    }
}