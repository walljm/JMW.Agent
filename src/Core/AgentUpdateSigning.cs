using System.Security.Cryptography;
using System.Text;

namespace JMW.Discovery.Core;

/// <summary>
/// Shared contract between the release signer (UpdateSign tool) and the agent's
/// update verifier (Agent's Updater). Both sides must build the exact same
/// canonical string from the same fields, so it lives here once instead of being
/// hand-rolled on each side where it could silently drift.
/// The canonical string binds a signature to one specific (version, filename,
/// hash, size) tuple: "version={version}\nfilename={filename}\nsha256={sha256}\nsize={size}\n".
/// </summary>
public static class AgentUpdateSigning
{
    /// <summary>The only signature algorithm this scheme supports. Sent/checked as an explicit
    /// string so the algorithm is negotiated, not assumed.</summary>
    public const string Algorithm = "ecdsa-p256-sha256";

    /// <summary>Builds the canonical metadata string that gets signed/verified.</summary>
    public static string BuildCanonicalString(string version, string filename, string sha256, long size) =>
        $"version={version}\nfilename={filename}\nsha256={sha256}\nsize={size}\n";

    /// <summary>Signs the canonical string for the given release metadata with an ECDSA P-256 private key.</summary>
    public static byte[] Sign(ECDsa privateKey, string version, string filename, string sha256, long size)
    {
        byte[] data = Encoding.UTF8.GetBytes(BuildCanonicalString(version, filename, sha256, size));
        return privateKey.SignData(data, HashAlgorithmName.SHA256);
    }

    /// <summary>Verifies a signature against the canonical string for the given release metadata.</summary>
    public static bool Verify(
        ECDsa publicKey,
        string version,
        string filename,
        string sha256,
        long size,
        byte[] signature
    )
    {
        byte[] data = Encoding.UTF8.GetBytes(BuildCanonicalString(version, filename, sha256, size));
        return publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }
}