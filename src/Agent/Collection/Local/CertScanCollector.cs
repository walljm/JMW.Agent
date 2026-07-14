using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Scans well-known host directories for X.509 certificate files and emits
/// facts about each unique certificate (keyed by SHA-256 fingerprint).
/// Locations scanned:
/// Linux/macOS : /etc/ssl/certs/, /etc/pki/tls/certs/, ~/.step/certs/,
/// /etc/nginx/ssl/, /etc/apache2/ssl/,
/// /etc/letsencrypt/live/*/fullchain.pem
/// Windows     : C:\ProgramData\step\config\ and IIS cert dirs
/// Fact keys (key dimension = SHA-256 fingerprint, lowercase hex):
/// Device[{deviceId}].Cert[{fp}].{SubjectDn|IssuerDn|NotBefore|NotAfter|Path|IsCA|SANs}
/// </summary>
public sealed class CertScanCollector : ILocalCollector
{
    public string Name => "cert-scan";
    public bool IsSupported => true;

    // Static directory list (no globs — those are handled separately for letsencrypt)
    private static readonly string[] LinuxDirs =
    [
        "/etc/ssl/certs",
        "/etc/pki/tls/certs",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".step", "certs"),
        "/etc/nginx/ssl",
        "/etc/apache2/ssl",
    ];

    private static readonly string[] WindowsDirs =
    [
        @"C:\ProgramData\step\config",
        @"C:\inetpub\ssl",
        @"C:\Windows\System32\inetsrv\config\ssl",
    ];

    private static readonly string[] Extensions = [".crt", ".pem", ".cer"];

    public Task<IReadOnlyList<Fact>> CollectAsync(string deviceId, CancellationToken ct)
    {
        List<Fact> facts = new();
        HashSet<string> seen = new(StringComparer.Ordinal); // fingerprints already emitted

        string[] dirs = OperatingSystem.IsWindows() ? WindowsDirs : LinuxDirs;

        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            ScanDirectory(dir, deviceId, seen, facts);
        }

        // Let's Encrypt glob: /etc/letsencrypt/live/*/fullchain.pem
        if (!OperatingSystem.IsWindows())
        {
            foreach (string pemPath in GlobLetsEncrypt())
            {
                TryLoadAndEmit(pemPath, deviceId, seen, facts);
            }
        }

        return Task.FromResult<IReadOnlyList<Fact>>(facts);
    }

    private static void ScanDirectory(string dir, string deviceId, HashSet<string> seen, List<Fact> facts)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                if (!IsKnownExtension(file))
                {
                    continue;
                }

                TryLoadAndEmit(file, deviceId, seen, facts);
            }
        }
        catch
        {
            /* permissions or I/O errors — skip directory */
        }
    }

    private static bool IsKnownExtension(string path)
    {
        string ext = Path.GetExtension(path);
        foreach (string e in Extensions)
        {
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GlobLetsEncrypt()
    {
        const string leBase = "/etc/letsencrypt/live";
        if (!Directory.Exists(leBase))
        {
            yield break;
        }

        foreach (string domain in Directory.EnumerateDirectories(leBase))
        {
            string fullchain = Path.Combine(domain, "fullchain.pem");
            if (File.Exists(fullchain))
            {
                yield return fullchain;
            }
        }
    }

    private static void TryLoadAndEmit(string path, string deviceId, HashSet<string> seen, List<Fact> facts)
    {
        try
        {
            using X509Certificate2 cert = X509CertificateLoader.LoadCertificateFromFile(path);

            string fp = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256))
                .ToLowerInvariant();

            if (!seen.Add(fp))
            {
                return; // already emitted from another path
            }

            bool isCA = false;
            List<string> sansBuffer = new(10);
            string? keyUsage = null;
            string? eku = null;

            foreach (X509Extension ext in cert.Extensions)
            {
                if (ext is X509BasicConstraintsExtension bc)
                {
                    isCA = bc.CertificateAuthority;
                }

                if (ext is X509SubjectAlternativeNameExtension san)
                {
                    foreach (string name in san.EnumerateDnsNames())
                    {
                        if (sansBuffer.Count >= 10)
                        {
                            break;
                        }

                        sansBuffer.Add(name);
                    }
                }

                if (ext is X509KeyUsageExtension ku)
                {
                    keyUsage = ku.KeyUsages.ToString();
                }

                if (ext is X509EnhancedKeyUsageExtension ekuExt)
                {
                    List<string> ekus = new();
                    foreach (Oid oid in ekuExt.EnhancedKeyUsages)
                    {
                        string label = oid.FriendlyName ?? oid.Value ?? string.Empty;
                        if (label.Length > 0)
                        {
                            ekus.Add(label);
                        }
                    }

                    if (ekus.Count > 0)
                    {
                        eku = string.Join(",", ekus);
                    }
                }
            }

            string[] keys = [deviceId, fp];
            facts.Add(Fact.Create(FactPaths.CertSubjectDn, keys, cert.SubjectName.Name));
            facts.Add(Fact.Create(FactPaths.CertIssuerDn, keys, cert.IssuerName.Name));
            facts.Add(Fact.Create(FactPaths.CertNotBefore, keys, cert.NotBefore.ToUniversalTime().ToString("o")));
            facts.Add(Fact.Create(FactPaths.CertNotAfter, keys, cert.NotAfter.ToUniversalTime().ToString("o")));
            facts.Add(Fact.Create(FactPaths.CertPath, keys, path));
            facts.Add(Fact.Create(FactPaths.CertIsCA, keys, isCA));
            facts.Add(Fact.Create(FactPaths.CertSerial, keys, cert.SerialNumber));
            facts.Add(
                Fact.Create(
                    FactPaths.CertSigAlgo,
                    keys,
                    cert.SignatureAlgorithm.FriendlyName ?? cert.SignatureAlgorithm.Value ?? ""
                )
            );
            int keySize = cert.GetRSAPublicKey()?.KeySize
             ?? cert.GetECDsaPublicKey()?.KeySize
             ?? cert.GetDSAPublicKey()?.KeySize ?? 0;
            if (keySize > 0)
            {
                facts.Add(Fact.Create(FactPaths.CertKeySize, keys, keySize));
            }

            if (keyUsage is not null)
            {
                facts.Add(Fact.Create(FactPaths.CertKeyUsage, keys, keyUsage));
            }

            if (eku is not null)
            {
                facts.Add(Fact.Create(FactPaths.CertEku, keys, eku));
            }

            if (sansBuffer.Count > 0)
            {
                facts.Add(Fact.Create(FactPaths.CertSANs, keys, string.Join(",", sansBuffer)));
            }
        }
        catch
        {
            /* invalid/unsupported cert format — skip */
        }
    }
}