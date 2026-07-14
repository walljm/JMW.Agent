using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers TLS-serving devices by probing ports 443 and 8443 on ARP-known
/// neighbors. Completes a TLS handshake without validating the presented
/// certificate (intentional — the goal is to read it, not trust it) and
/// records the subject, issuer, serial, expiration, and CN/DNS name.
/// Useful for identifying HTTPS-capable devices and flagging expiring or
/// self-signed certificates. Source tag: "tls-cert".
/// </summary>
public sealed class TlsCertScanner : UnicastScannerBase
{
    public override string Name => "tls-cert";

    private static readonly int[] Ports = [443, 8443];

    protected override async Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct)
    {
        foreach (int port in Ports)
        {
            DiscoveredDevice? device = await TryProbeTlsAsync(ip, port, ct);
            if (device is not null)
            {
                return device;
            }
        }

        return null;
    }

    private static async Task<DiscoveredDevice?> TryProbeTlsAsync(string ip, int port, CancellationToken ct)
    {
        try
        {
            using TcpClient? tcpClient = await SocketProbe.TryConnectAsync(ip, port, 2000, ct);
            if (tcpClient is null)
            {
                return null;
            }

            using NetworkStream netStream = tcpClient.GetStream();
#pragma warning disable CA5359 // cert scanning is the purpose — accept all certs intentionally
            using SslStream sslStream = new(
                netStream,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true
            );

            await sslStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = ip,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
                // ReSharper disable once PossiblyMistakenUseOfCancellationToken
                ct
            );
#pragma warning restore CA5359

            X509Certificate? remoteCert = sslStream.RemoteCertificate;
            if (remoteCert is null)
            {
                return null;
            }

            X509Certificate2 cert = new(remoteCert);

            Dictionary<string, string> attributes = new()
            {
                ["tls.subject"] = cert.Subject,
                ["tls.issuer"] = cert.Issuer,
                // ISO-8601 UTC so the server can promote it to a real timestamp fact (sortable /
                // date-queryable) rather than a locale-formatted display string.
                ["tls.expires"] = cert.NotAfter.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["tls.serial"] = cert.GetSerialNumberString(),
            };

            string dnsName = cert.GetNameInfo(X509NameType.DnsName, false);
            string hostname = dnsName;

            if (string.IsNullOrWhiteSpace(hostname))
            {
                hostname = ExtractCn(cert.Subject);
            }

            string cn = ExtractCn(cert.Subject);
            if (!string.IsNullOrWhiteSpace(cn))
            {
                attributes["tls.cn"] = cn;
            }

            return new DiscoveredDevice
            {
                IpAddress = ip,
                Hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname,
                Source = "tls-cert",
                Attributes = attributes,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractCn(string subject)
    {
        foreach (string part in subject.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[3..].Trim();
            }
        }

        return "";
    }
}