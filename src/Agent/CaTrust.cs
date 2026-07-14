using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent;

/// <summary>
/// Process-wide store of additional trusted root CAs delivered by the server in the
/// heartbeat config (<see cref="JMW.Discovery.Core.HeartbeatConfig.TrustedCaCertificates" />).
/// Validating collectors — those that connect to operator-configured HTTPS endpoints and
/// must authenticate the server certificate — route TLS validation through
/// <see cref="Validate" /> (usually via <see cref="CreateHandler" />). A certificate is
/// accepted when it passes the OS system trust store OR chains to one of the configured
/// CAs. Hostname mismatches remain fatal — a private CA must not paper those over.
/// The trusted set is mutable: collectors build their <see cref="HttpClient" /> once at
/// startup, but CAs arrive later via heartbeat, so the callback reads the current snapshot
/// on each handshake. Snapshots are swapped atomically; readers never lock.
/// This is deliberately separate from the agent's own server channel, which authenticates
/// the server by SHA-256 public-key pinning rather than CA trust. Discovery probes that
/// fingerprint unknown devices continue to accept any certificate and must not use this.
/// </summary>
public static class CaTrust
{
    private static volatile X509Certificate2Collection _roots = new();
    private static readonly ILogger _logger = AgentLog.Factory.CreateLogger("CaTrust");

    /// <summary>
    /// Replaces the trusted-CA set from PEM-encoded certificates. A single entry may hold a
    /// chain (root + intermediates); all certificates in it are trusted as anchors. Malformed
    /// entries are logged and skipped. Null/empty clears the set (system trust only).
    /// Safe to call on every heartbeat cycle.
    /// </summary>
    public static void Update(IReadOnlyList<string>? pemCertificates)
    {
        X509Certificate2Collection next = new();
        if (pemCertificates is not null)
        {
            foreach (string pem in pemCertificates)
            {
                if (string.IsNullOrWhiteSpace(pem))
                {
                    continue;
                }

                try
                {
                    next.ImportFromPem(pem);
                }
                catch (Exception ex)
                {
                    CaTrustLog.InvalidCertificate(_logger, ex);
                }
            }
        }

        _roots = next;
        CaTrustLog.Updated(_logger, next.Count);
    }

    /// <summary>
    /// Builds a <see cref="SocketsHttpHandler" /> whose TLS validation trusts the OS system
    /// store plus any CAs configured via <see cref="Update" />. Use this for collectors that
    /// talk to operator-run HTTPS services signed by a private CA.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(TimeSpan? pooledConnectionLifetime = null) =>
        new()
        {
            PooledConnectionLifetime = pooledConnectionLifetime ?? TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = Validate,
            },
        };

    /// <summary>
    /// <see cref="RemoteCertificateValidationCallback" /> for validating collectors. Accepts
    /// the certificate when system trust succeeds, or when it chains to a configured CA.
    /// </summary>
    public static bool Validate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors
    )
    {
        if (errors == SslPolicyErrors.None)
        {
            // System trust store already validated the chain.
            return true;
        }

        // Only override an untrusted-root/chain error. A hostname mismatch or an absent
        // certificate is a genuine failure that a custom CA must not override.
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
        {
            return false;
        }

        X509Certificate2Collection roots = _roots;
        if (roots.Count == 0 || certificate is not X509Certificate2 leaf)
        {
            return false;
        }

        using X509Chain custom = new();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(roots);
        // Private homelab CAs typically publish no CRL/OCSP endpoint; skipping revocation
        // avoids failing closed on an unreachable distribution point.
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        // Supply any intermediates the server presented so the chain can build even when
        // only the root (not the intermediate) was configured as a trusted CA.
        if (chain is not null)
        {
            foreach (X509ChainElement element in chain.ChainElements)
            {
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        return custom.Build(leaf);
    }
}

internal static partial class CaTrustLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Trusted CA set updated: {Count} certificate(s).")]
    internal static partial void Updated(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped a malformed trusted CA certificate.")]
    internal static partial void InvalidCertificate(ILogger logger, Exception ex);
}