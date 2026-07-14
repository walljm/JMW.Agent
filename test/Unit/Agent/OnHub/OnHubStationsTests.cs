using Google.Protobuf;

using JMW.Discovery.Agent.Collection.Device.OnHub;
using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;

namespace JMW.Discovery.UnitTests.Agent.OnHub;

public sealed class OnHubStationsTests
{
    private static OnHubStation? ByIp(IReadOnlyList<OnHubStation> stations, string ip) =>
        stations.FirstOrDefault(s => s.Ip == ip);

    [Fact]
    public void Extract_JoinsMacAndNetworkStateOnIp()
    {
        DiagnosticReport report = DiagnosticReport.Parser.ParseFrom(OnHubTestData.SampleReport());

        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(report);

        // .60 (mac only, no rich net data), .70 (ap-show only), .230 (full). .99 disconnected.
        Assert.Equal(3, stations.Count);

        OnHubStation? macbook = ByIp(stations, "192.168.1.230");
        Assert.NotNull(macbook);
        Assert.Equal("f01898ab2cd*", macbook.Mac);
        Assert.Equal("walljm-macbook-intel.local", macbook.Hostname);
        Assert.Equal("MacBookPro14,2", macbook.Model);
        Assert.True(macbook.Connected);

        OnHubStation? sixty = ByIp(stations, "192.168.1.60");
        Assert.NotNull(sixty);
        Assert.Equal("00e0bf1fc40*", sixty.Mac);
        Assert.Null(sixty.Hostname); // no mdns_name
        Assert.Null(sixty.Model); // device_model empty
    }

    [Fact]
    public void Extract_ApShowOnlyStation_EmittedWithMac()
    {
        DiagnosticReport report = DiagnosticReport.Parser.ParseFrom(OnHubTestData.SampleReport());
        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(report);

        OnHubStation? seventy = ByIp(stations, "192.168.1.70");
        Assert.NotNull(seventy);
        Assert.Equal("10bf485e6ca*", seventy.Mac);
        Assert.Null(seventy.Hostname);
    }

    [Fact]
    public void Extract_DisconnectedStation_Excluded()
    {
        DiagnosticReport report = DiagnosticReport.Parser.ParseFrom(OnHubTestData.SampleReport());
        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(report);

        Assert.Null(ByIp(stations, "192.168.1.99"));
    }

    [Fact]
    public void Extract_NetworkStateOnlyConnectedStation_EmittedWithHostnameNoMac()
    {
        // A station present only in networkState (no ap-show entry, so no MAC).
        string networkState =
            """
            network_service_state {
              station_state_updates {
                station_state_update {
                  station_info {
                    mdns_name: "printer.local"
                    connected: true
                    ip_addresses: "192.168.1.50"
                  }
                }
              }
            }
            """;
        byte[] bytes = OnHubTestData.BuildReport("ABCD1234", networkState); // no commandOutputs
        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(DiagnosticReport.Parser.ParseFrom(bytes));

        OnHubStation? printer = ByIp(stations, "192.168.1.50");
        Assert.NotNull(printer);
        Assert.Null(printer.Mac);
        Assert.Equal("printer.local", printer.Hostname);
    }

    [Fact]
    public void Extract_MaskedHostname_Dropped()
    {
        string networkState =
            """
            station_state_update {
              station_info {
                dhcp_hostname: "********************"
                connected: true
                ip_addresses: "192.168.1.51"
              }
            }
            """;
        // dhcp_hostname is masked and mdns_name absent → no hostname, and no mac → not emitted.
        byte[] bytes = OnHubTestData.BuildReport("ABCD1234", networkState);
        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(DiagnosticReport.Parser.ParseFrom(bytes));

        Assert.Null(ByIp(stations, "192.168.1.51"));
    }

    [Fact]
    public void Extract_FullyMaskedMac_NotEmittedAsMac()
    {
        string apShow =
            """
            station_info {
              mac_address: "************"
              ipv4_addresses: "192.168.1.80"
            }
            """;
        byte[] bytes = OnHubTestData.BuildReport(
            "ABCD1234",
            networkState: "",
            (OnHubTestData.ApShowCommand, apShow)
        );
        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(DiagnosticReport.Parser.ParseFrom(bytes));

        // No usable MAC, no hostname, no model → nothing to report.
        Assert.Null(ByIp(stations, "192.168.1.80"));
    }

    [Fact]
    public void Extract_ObscuredMacFallback_JoinsViaStationIdMapping()
    {
        // ap-show reports the station at .40; networkState reports the same station_id
        // (per stationIdMappings) at a different IP, .41 — simulating the two blobs
        // disagreeing on IP for the same client. Only the field-12 obscured-MAC →
        // station_id lookup recovers the rich detail for the ap-show IP.
        const string apShow =
            """
            station_info {
              mac_address: "aabbcc11223*"
              ipv4_addresses: "192.168.1.40"
            }
            """;
        const string networkState =
            """
            station_state_update {
              station_info {
                station_id: "DEADBEEFCAFEBABE0000000000000000000000000000000000000000000001"
                mdns_name: "widget.local"
                connected: true
                ip_addresses: "192.168.1.41"
              }
            }
            """;

        DiagnosticReport report = new() { DeviceId = "ABCD1234", NetworkState = networkState };
        report.CommandOutputs.Add(new CommandOutput { Command = OnHubTestData.ApShowCommand, Output = apShow });
        report.StationIdMappings.Add(
            new StationIdMapping
            {
                StationId = "DEADBEEFCAFEBABE0000000000000000000000000000000000000000000001",
                ObscuredMac = "aabbcc11223*",
            }
        );

        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(report);

        OnHubStation? station = ByIp(stations, "192.168.1.40");
        Assert.NotNull(station);
        Assert.Equal("aabbcc11223*", station.Mac);
        Assert.Equal("widget.local", station.Hostname);
    }

    [Fact]
    public void Extract_MeshGroupChild_FlaggedAsMeshNodeAndNotDropped()
    {
        // A mesh point: listed in mesh_group.node_info (networkState/field 16's shape —
        // NOT the differently-named group.root/child in networkConfig/field 5, verified
        // against a live capture), connected, Google's OUI, but no mDNS name (masked), no
        // model, no ap-show/ARP entry — every other signal is empty, so without the mesh
        // flag this would be dropped entirely.
        const string networkState =
            """
            mesh_group {
              id: "9501a371-1a44-459f-96d2-24921ff0d728"
              node_info {
                id: "9C8B3BCECF47C5826E9492948061D17F539D21C8C520B0641B65D24BFADCBDB3"
                ip_address: "192.168.1.215"
              }
            }
            station_state_update {
              station_info {
                station_id: "9C8B3BCECF47C5826E9492948061D17F539D21C8C520B0641B65D24BFADCBDB3"
                mdns_name: "********************************.local"
                connected: true
                ip_addresses: "192.168.1.215"
                wireless: false
                dhcp_hostname: ""
                oui: "703acb"
                guest: false
                device_model: ""
              }
            }
            """;
        byte[] bytes = OnHubTestData.BuildReport("ABCD1234", networkState);
        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(DiagnosticReport.Parser.ParseFrom(bytes));

        OnHubStation? meshPoint = ByIp(stations, "192.168.1.215");
        Assert.NotNull(meshPoint);
        Assert.True(meshPoint.IsMeshNode);
        Assert.Equal("703acb", meshPoint.Oui);
        Assert.Null(meshPoint.Hostname); // masked
    }

    [Fact]
    public void Extract_OrdinaryClient_NotFlaggedAsMeshNode()
    {
        // Sanity check: a normal client (not listed in any group) is unaffected.
        DiagnosticReport report = DiagnosticReport.Parser.ParseFrom(OnHubTestData.SampleReport());
        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(report);

        OnHubStation? macbook = ByIp(stations, "192.168.1.230");
        Assert.NotNull(macbook);
        Assert.False(macbook.IsMeshNode);
    }

    [Fact]
    public void Extract_ArpTableAsFile_ParsedFromFieldTwoNotCommandOutputs()
    {
        // A real capture carries /proc/net/arp as a field-2 File, not a field-9
        // CommandOutput — OnHubTestData's ArpTable fixture (used elsewhere) models the
        // CommandOutput path; this proves the File path independently.
        const string arpTable =
            "IP address       HW type     Flags       HW address            Mask     Device\n"
          + "192.168.1.215    0x1         0x2         703acb9c8b3*          *        br-lan\n";

        DiagnosticReport report = new() { DeviceId = "ABCD1234", NetworkState = "" };
        report.Files.Add(
            new JMW.Discovery.Agent.Collection.Device.OnHub.Proto.File
            {
                Path = "/proc/net/arp",
                Content = ByteString.CopyFromUtf8(arpTable),
            }
        );

        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(report);

        OnHubStation? station = ByIp(stations, "192.168.1.215");
        Assert.NotNull(station);
        Assert.Equal("703acb9c8b3*", station.Mac);
    }

    [Fact]
    public void Extract_MeshRoutingTable_ResolvesServingMeshApRealBssid()
    {
        // Real values from a live capture: client 00e0bf1fc40* is relayed by mesh point
        // 723acb820d9* (obscured) per mpp dump; stationIdMappings ties that obscured MAC
        // to station_id 820D97...; mesh_node_info ties that same station_id to the mesh
        // point's real (unobscured) bssid.
        const string apShow =
            """
            station_info {
              mac_address: "00e0bf1fc40*"
              ipv4_addresses: "192.168.1.60"
            }
            """;
        const string mppDump =
            "DEST ADDR         PROXY NODE        IFACE\n"
          + "00e0bf1fc40*      723acb820d9*      mesh-5000mhz\n";
        // mesh_node_info lives in wanInfo (field 8) — NOT networkState (field 16),
        // verified against a live capture.
        const string wanInfo =
            """
            mesh_node_info {
              bssid: "72:3a:cb:ea:a8:01"
              shmac: "820D97E142579199A46924567AD5A6870608545D93E6CCCBCD7425253ED5A0EB"
            }
            """;

        DiagnosticReport report = new() { DeviceId = "ABCD1234", WanInfo = wanInfo };
        report.CommandOutputs.Add(new CommandOutput { Command = OnHubTestData.ApShowCommand, Output = apShow });
        report.CommandOutputs.Add(
            new CommandOutput { Command = "/usr/sbin/iw dev mesh-5000mhz mpp dump", Output = mppDump }
        );
        report.StationIdMappings.Add(
            new StationIdMapping
            {
                StationId = "820D97E142579199A46924567AD5A6870608545D93E6CCCBCD7425253ED5A0EB",
                ObscuredMac = "723acb820d9*",
            }
        );

        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(report);

        OnHubStation? station = ByIp(stations, "192.168.1.60");
        Assert.NotNull(station);
        Assert.Equal("72:3a:cb:ea:a8:01", station.MeshApBssid);
    }

    [Fact]
    public void Extract_NoMppDumpEntry_MeshApBssidIsNull()
    {
        DiagnosticReport report = DiagnosticReport.Parser.ParseFrom(OnHubTestData.SampleReport());
        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(report);

        OnHubStation? macbook = ByIp(stations, "192.168.1.230");
        Assert.NotNull(macbook);
        Assert.Null(macbook.MeshApBssid);
    }

    [Fact]
    public void Extract_DhcpReservedStation_FlaggedIsDhcpReserved()
    {
        // Real values from a live capture: dhcp_reservations.dhcp_reservation.id is a
        // station_id, cross-referenced against station_state_update.
        const string networkState =
            """
            dhcp_reservations {
              dhcp_reservation {
                id: "ABC531D3B8BE0845B067980EB3C5D10C4C774A0683179E027C7DB3FE57A48E59"
                ip_address: "192.168.1.224"
              }
            }
            station_state_update {
              station_info {
                station_id: "ABC531D3B8BE0845B067980EB3C5D10C4C774A0683179E027C7DB3FE57A48E59"
                mdns_name: "kitchen-printer.local"
                connected: true
                ip_addresses: "192.168.1.224"
              }
            }
            """;
        byte[] bytes = OnHubTestData.BuildReport("ABCD1234", networkState);
        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(DiagnosticReport.Parser.ParseFrom(bytes));

        OnHubStation? station = ByIp(stations, "192.168.1.224");
        Assert.NotNull(station);
        Assert.True(station.IsDhcpReserved);
        Assert.Equal("192.168.1.224", station.DhcpReservedIp);
    }

    [Fact]
    public void Extract_DhcpReservationWithNoIpAddress_StillFlaggedButIpIsNull()
    {
        // Defensive: a reservation entry missing ip_address must not change the
        // IsDhcpReserved flag's meaning (it's keyed on id presence, not ip_address).
        const string networkState =
            """
            dhcp_reservations {
              dhcp_reservation {
                id: "ABC531D3B8BE0845B067980EB3C5D10C4C774A0683179E027C7DB3FE57A48E59"
              }
            }
            station_state_update {
              station_info {
                station_id: "ABC531D3B8BE0845B067980EB3C5D10C4C774A0683179E027C7DB3FE57A48E59"
                mdns_name: "kitchen-printer.local"
                connected: true
                ip_addresses: "192.168.1.224"
              }
            }
            """;
        byte[] bytes = OnHubTestData.BuildReport("ABCD1234", networkState);
        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(DiagnosticReport.Parser.ParseFrom(bytes));

        OnHubStation? station = ByIp(stations, "192.168.1.224");
        Assert.NotNull(station);
        Assert.True(station.IsDhcpReserved);
        Assert.Null(station.DhcpReservedIp);
    }

    [Fact]
    public void Extract_OrdinaryClient_NotFlaggedDhcpReserved()
    {
        DiagnosticReport report = DiagnosticReport.Parser.ParseFrom(OnHubTestData.SampleReport());
        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(report);

        OnHubStation? macbook = ByIp(stations, "192.168.1.230");
        Assert.NotNull(macbook);
        Assert.False(macbook.IsDhcpReserved);
        Assert.Null(macbook.DhcpReservedIp);
    }

    [Fact]
    public void Extract_HostapdLog_AttachesLastActiveAndRoamingByObscuredMac()
    {
        DiagnosticReport report = DiagnosticReport.Parser.ParseFrom(OnHubTestData.SampleReport());
        report.Files.Add(
            new JMW.Discovery.Agent.Collection.Device.OnHub.Proto.File
            {
                Path = "/var/log/messages",
                // f01898ab2cd* is the macbook-intel's obscured MAC (ap-show, IP .230).
                Content = ByteString.CopyFromUtf8(
                    """
                    2026-07-13T20:11:41.300311Z INFO hostapd[2513]: wlan-2400mhz: STA f01898ab2cd*      IEEE 802.11: Station f01898ab2cd*      has been active 0s ago
                    2026-07-13T20:30:02.515930Z INFO hostapd[2513]: guest-5000mhz: STA f01898ab2cd*      IAPP: Received IAPP ADD-notify (seq# 0) from 192.168.1.217:3517 (STA not found)
                    """
                ),
            }
        );

        IReadOnlyList<OnHubStation> stations = OnHubStations.Extract(report);

        OnHubStation? macbook = ByIp(stations, "192.168.1.230");
        Assert.NotNull(macbook);
        Assert.Equal(DateTimeOffset.Parse("2026-07-13T20:11:41.300311Z"), macbook.LastActiveAt);
        Assert.Equal("wlan-2400mhz", macbook.LastActiveInterface);
        Assert.Equal(DateTimeOffset.Parse("2026-07-13T20:30:02.515930Z"), macbook.LastRoamingAt);
        Assert.Equal("192.168.1.217", macbook.LastRoamingApIp);
    }

    [Theory]
    [InlineData("00e0bf1fc40*", "00e0bf1fc40*")] // canonical
    [InlineData("00E0BF1FC40*", "00e0bf1fc40*")] // uppercase → lowercased
    [InlineData("  10bf485e6ca*  ", "10bf485e6ca*")] // trimmed
    [InlineData("************", null)] // fully masked
    [InlineData("00e0bf1fc401", null)] // no trailing '*'
    [InlineData("00e0bf1fc4*", null)] // too short (11 chars)
    [InlineData("00e0bf1fc400*", null)] // too long (13 chars)
    [InlineData("00e0bg1fc40*", null)] // non-hex nibble ('g')
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeObscuredMac_TableDriven(string? input, string? expected) =>
        Assert.Equal(expected, OnHubStations.NormalizeObscuredMac(input));
}