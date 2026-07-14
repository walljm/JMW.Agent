using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects network interface facts from the local host using the .NET
/// NetworkInterface API — no /proc parsing or subprocess required.
/// </summary>
public sealed class NetworkCollector : ILocalCollector
{
    public string Name => "network";
    public bool IsSupported => true; // NetworkInterface works on Linux, Windows, macOS

    public Task<IReadOnlyList<Fact>> CollectAsync(string deviceId, CancellationToken ct)
    {
        List<Fact> facts = new();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Skip purely virtual loopback interfaces.
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            string name = nic.Name;
            string[] ifKeys = [deviceId, name];

            facts.Add(Fact.Create(FactPaths.InterfaceName, ifKeys, nic.Name));
            facts.Add(Fact.Create(FactPaths.InterfaceUp, ifKeys, nic.OperationalStatus == OperationalStatus.Up));
            facts.Add(Fact.Create(FactPaths.InterfaceMTU, ifKeys, nic.GetIPProperties().GetIPv4Properties().Mtu));
            facts.Add(Fact.Create(FactPaths.InterfaceType, ifKeys, nic.NetworkInterfaceType.ToString()));

            byte[] mac = nic.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length == 6)
            {
                facts.Add(
                    Fact.Create(
                        FactPaths.InterfaceMAC,
                        ifKeys,
                        MacFormat.FromBytes(mac)
                    )
                );
            }

            // Speed: reported in bits/sec by .NET
            if (nic.Speed > 0)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceSpeedBps, ifKeys, nic.Speed));
            }

            IPInterfaceProperties ipProps = nic.GetIPProperties();

            foreach (UnicastIPAddressInformation addr in ipProps.UnicastAddresses)
            {
                IPAddress ip = addr.Address;
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    facts.Add(Fact.Create(FactPaths.InterfaceIPv4, ifKeys, $"{ip}/{addr.PrefixLength}"));
                }
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6
                 && !ip.IsIPv6LinkLocal)
                {
                    facts.Add(Fact.Create(FactPaths.InterfaceIPv6, ifKeys, $"{ip}/{addr.PrefixLength}"));
                }
            }

            // DNS servers are per-adapter on Windows; on Linux they're global.
            foreach (IPAddress dns in ipProps.DnsAddresses)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceDns, ifKeys, dns.ToString()));
            }

            // T2-8: gateway(s) + DHCP server. DhcpServerAddresses throws on Linux (Windows-only).
            foreach (GatewayIPAddressInformation gw in ipProps.GatewayAddresses)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceGateway, ifKeys, gw.Address.ToString()));
            }

            if (OperatingSystem.IsWindows())
            {
                foreach (IPAddress dhcp in ipProps.DhcpServerAddresses)
                {
                    facts.Add(Fact.Create(FactPaths.InterfaceDhcpServer, ifKeys, dhcp.ToString()));
                }
            }

            // Traffic counters — not available on all platforms (returns zeros on macOS).
            if (!OperatingSystem.IsMacOS())
            {
                IPInterfaceStatistics stats = nic.GetIPStatistics();
                facts.Add(Fact.Create(FactPaths.InterfaceRxBytes, ifKeys, stats.BytesReceived));
                facts.Add(Fact.Create(FactPaths.InterfaceTxBytes, ifKeys, stats.BytesSent));
            }
        }

        // Global DNS on Linux (resolv.conf). On Windows this is per-adapter above.
        if (OperatingSystem.IsLinux())
        {
            (List<string> servers, List<string> search) = ReadResolvConf();
            for (int i = 0; i < servers.Count; i++)
            {
                facts.Add(Fact.Create(FactPaths.NetworkDnsServer, [deviceId, i.ToString()], servers[i]));
            }

            for (int i = 0; i < search.Count; i++)
            {
                facts.Add(Fact.Create(FactPaths.NetworkDnsSearch, [deviceId, i.ToString()], search[i]));
            }
        }

        return Task.FromResult<IReadOnlyList<Fact>>(facts);
    }

    private static (List<string> Servers, List<string> Search) ReadResolvConf()
    {
        List<string> servers = new();
        List<string> search = new();

        try
        {
            foreach (string line in File.ReadLines("/etc/resolv.conf"))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                {
                    continue;
                }

                string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "nameserver": servers.Add(parts[1]); break;
                    case "search":
                    case "domain": search.AddRange(parts[1..]); break;
                }
            }
        }
        catch
        {
            /* best-effort */
        }

        return (servers, search);
    }
}