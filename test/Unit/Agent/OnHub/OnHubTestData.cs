using System.IO.Compression;

using Google.Protobuf;

using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;

namespace JMW.Discovery.UnitTests.Agent.OnHub;

/// <summary>
/// Builds small synthetic Google Wifi diagnostic-report protobufs and holds
/// representative text-format blobs, so tests never depend on a multi-megabyte
/// captured report.
/// </summary>
internal static class OnHubTestData
{
    /// <summary>The <c>ap-show --network_runtime_state</c> command string.</summary>
    public const string ApShowCommand = "/usr/sbin/ap-show --network_runtime_state";

    /// <summary>
    /// ap-show output: obscured MACs keyed to IPs. Mirrors the real firmware —
    /// last hex nibble masked with '*', station_id fully masked.
    /// </summary>
    public const string ApShowOutput =
        """
        station_info {
          mac_address: "00e0bf1fc40*"
          station_id: "****************************************************************"
          ipv4_addresses: "192.168.1.60"
          wireless_interface: ""
        }
        station_info {
          mac_address: "10bf485e6ca*"
          station_id: "****************************************************************"
          ipv4_addresses: "192.168.1.70"
          wireless_interface: ""
        }
        station_info {
          mac_address: "f01898ab2cd*"
          station_id: "****************************************************************"
          ipv4_addresses: "192.168.1.230"
          wireless_interface: "wlan-2400mhz"
        }
        station_info {
          mac_address: "aabbccddee0*"
          station_id: "****************************************************************"
          ipv4_addresses: "192.168.1.99"
          wireless_interface: ""
        }
        """;

    /// <summary>
    /// networkState blob: station_state_updates with connected flag, IP, mDNS name,
    /// masked dhcp_hostname, and device model. .60/.70/.230 connected; .99 not.
    /// </summary>
    public const string NetworkState =
        """
        state_seq_no: "9784"
        timestamp {
          seconds: 1783458962
        }
        network_service_state {
          station_state_updates {
            station_state_update {
              station_info {
                station_id: "1FC40862429D9DFF95BF0C6FA8C9251186A5A1B8788C4C51D8D6BC85C4968FF9"
                connected: true
                ip_addresses: "192.168.1.60"
                wireless: false
                dhcp_hostname: ""
                device_model: ""
              }
            }
            station_state_update {
              station_info {
                station_id: "EB43D6748CC2BD2583B6099019B42D6230F5C4EFA49CEEE9F5C4C0B2A7F78444"
                mdns_name: "walljm-macbook-intel.local"
                connected: true
                ip_addresses: "192.168.1.230"
                dhcp_hostname: "********************"
                device_model: "MacBookPro14,2"
              }
            }
            station_state_update {
              station_info {
                station_id: "AAAA0862429D9DFF95BF0C6FA8C9251186A5A1B8788C4C51D8D6BC85C49600AA"
                connected: false
                ip_addresses: "192.168.1.99"
              }
            }
          }
        }
        """;

    public const string DeviceId = "ADEC2AD42ACEF8CB5384A6D7CFDA90A3";

    /// <summary>Builds a report protobuf with the given command outputs, networkState, and device id.</summary>
    public static byte[] BuildReport(
        string deviceId,
        string networkState,
        params (string Command, string Output)[] commands
    )
    {
        DiagnosticReport report = new()
        {
            DeviceId = deviceId,
            NetworkState = networkState,
            // An unrelated field, to prove extraction ignores everything but 9/16/21.
            StormVersion = "Google_Gale.8281.47.0",
        };

        foreach ((string command, string cmdOutput) in commands)
        {
            report.CommandOutputs.Add(
                new CommandOutput
                {
                    Command = command,
                    Output = cmdOutput,
                }
            );
        }

        return report.ToByteArray();
    }

    /// <summary>A ready-made report using the canned blobs above.</summary>
    public static byte[] SampleReport() =>
        BuildReport(DeviceId, NetworkState, (ApShowCommand, ApShowOutput));

    /// <summary>gzip-wraps bytes the way the device serves the report body.</summary>
    public static byte[] Gzip(byte[] data)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress))
        {
            gzip.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }
}