using System.Net.Sockets;
using System.Text;

using Renci.SshNet;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers hosts by probing port 22 on ARP-known neighbors.
/// Reads the SSH version banner and captures the server host-key fingerprint
/// via a key-exchange probe (auth is not attempted).
/// Source tag: "ssh-banner".
/// </summary>
public sealed class SshBannerScanner : UnicastScannerBase
{
    public override string Name => "ssh-banner";

    protected override int MaxConcurrency => 20;

    protected override async Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct)
    {
        // Step 1: TCP-read the SSH version banner (server sends it immediately on connect).
        string? banner = await ReadBannerAsync(ip, ct);
        if (banner is null || !banner.StartsWith("SSH-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Step 2: Key-exchange probe to capture the host-key fingerprint.
        string? fingerprint = await ReadHostKeyFingerprintAsync(ip, ct);

        Dictionary<string, string> attrs = new()
        {
            ["ssh.banner"] = banner,
        };

        if (fingerprint is not null)
        {
            attrs["ssh.host-key-fp"] = fingerprint;
        }

        return new DiscoveredDevice
        {
            IpAddress = ip,
            Source = "ssh-banner",
            Attributes = attrs,
        };
    }

    private static async Task<string?> ReadBannerAsync(string ip, CancellationToken ct)
    {
        try
        {
            using TcpClient? tcp = await SocketProbe.TryConnectAsync(ip, 22, 3000, ct);
            if (tcp is null)
            {
                return null;
            }

            tcp.ReceiveTimeout = 2000;

            using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(3));

            using NetworkStream stream = tcp.GetStream();
            byte[] buf = new byte[256];

            int total = 0;
            while (total < buf.Length)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), connectCts.Token);
                }
                catch
                {
                    break;
                }

                if (read == 0)
                {
                    break;
                }

                total += read;

                // Banner ends at the first CRLF or LF.
                int nl = Array.IndexOf(buf, (byte)'\n', 0, total);
                if (nl >= 0)
                {
                    int end = nl;
                    if (end > 0 && buf[end - 1] == '\r')
                    {
                        end--;
                    }

                    return Encoding.ASCII.GetString(buf, 0, end).Trim();
                }
            }

            return total > 0 ? Encoding.ASCII.GetString(buf, 0, total).Trim() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ReadHostKeyFingerprintAsync(string ip, CancellationToken ct)
    {
        string? fingerprint = null;

        try
        {
            await Task.Run(
                () =>
                {
                    ConnectionInfo info = new(
                        ip,
                        22,
                        "_probe_",
                        new PasswordAuthenticationMethod("_probe_", "_probe_")
                    )
                    {
                        Timeout = TimeSpan.FromSeconds(4),
                    };

                    using SshClient client = new(info);
                    client.HostKeyReceived += (_, e) =>
                    {
                        // OpenSSH-canonical SHA-256 host-key fingerprint: "sha256:<base64>"
                        // (no padding), matching `ssh-keygen -lf`. The algorithm prefix lets
                        // FingerprintNormalizer canonicalize it into a stable device fingerprint.
                        fingerprint = $"sha256:{e.FingerPrintSHA256}";
                        e.CanTrust = false; // abort after key received — don't wait for auth
                    };

                    try
                    {
                        client.Connect();
                    }
                    catch
                    {
                        // Auth failure and connection abort are both expected.
                    }
                },
                ct
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        return fingerprint;
    }
}