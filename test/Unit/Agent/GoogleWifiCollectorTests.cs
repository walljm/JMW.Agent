using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Google.Protobuf;

using JMW.Discovery.Agent;
using JMW.Discovery.Agent.Collection;
using JMW.Discovery.Agent.Collection.Device;
using JMW.Discovery.Agent.Collection.Device.OnHub;
using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;
using JMW.Discovery.Core;
using JMW.Discovery.UnitTests.Agent.OnHub;

namespace JMW.Discovery.UnitTests.Agent;

public sealed class GoogleWifiCollectorTests
{
    [Fact]
    public void CanCollect_MatchesGoogleWifiProtocol()
    {
        GoogleWifiCollector collector = new(new OnHubClient(new HttpClient(new StubHandler(_ => NotFound()))));

        Assert.True(
            collector.CanCollect(
                new Target
                {
                    Endpoint = "192.168.1.1",
                    CollectorType = "google-wifi",
                }
            )
        );
        Assert.True(
            collector.CanCollect(
                new Target
                {
                    Endpoint = "192.168.1.1",
                    CollectorType = "GOOGLE-WIFI",
                }
            )
        );
        Assert.False(
            collector.CanCollect(
                new Target
                {
                    Endpoint = "192.168.1.1",
                    CollectorType = "ssh",
                }
            )
        );
    }

    [Fact]
    public async Task Collect_EmitsRouterFactsAndDiscoveredStations()
    {
        StubHandler handler = new(SampleRoute);
        FakeContext ctx = new();
        List<Fact> facts = (await Collect(handler, ctx)).ToList();

        // ── Identity ──
        Assert.NotNull(ctx.ResolvedFingerprints);
        Fingerprint fp = Assert.Single(ctx.ResolvedFingerprints);
        Assert.Equal(FingerprintType.GoogleWifiDeviceId, fp.Type);
        Assert.Equal(OnHubTestData.DeviceId, fp.Value);

        // ── Router facts (from /status + report field 21) ──
        Assert.Equal("Google", Value(facts, "Device[_probe_].Vendor"));
        Assert.Equal("router", Value(facts, "Device[_probe_].Kind"));
        Assert.Equal("Google", Value(facts, "Device[_probe_].Hardware.SystemVendor"));
        Assert.Equal(OnHubTestData.DeviceId, Value(facts, "Device[_probe_].Hardware.SystemSerial"));
        Assert.Equal("ACc3d", Value(facts, "Device[_probe_].Hardware.SystemModel"));
        Assert.Equal("14150.376.32", Value(facts, "Device[_probe_].OS.Version"));
        Assert.Equal(123456L, facts.First(f => f.Id == "Device[_probe_].System.UptimeSeconds").Value.AsLong());
        // No /etc/lsb-release in SampleRoute → the a-priori fallback fires (OnHub firmware
        // is ChromiumOS-derived Linux). With lsb-release present the observed name wins —
        // see Collect_EmitsRichClientAndApFacts.
        Assert.Equal("linux", Value(facts, "Device[_probe_].OS.Family"));
        Assert.Equal("ChromiumOS", Value(facts, "Device[_probe_].OS.Distro"));

        // ── Discovered stations keyed by IP ──
        Assert.Equal("00e0bf1fc40*", Value(facts, "Device[_probe_].Discovered[192.168.1.60].ObscuredMAC"));
        Assert.Equal("google-wifi", Value(facts, "Device[_probe_].Discovered[192.168.1.60].Sources"));
        Assert.Equal("f01898ab2cd*", Value(facts, "Device[_probe_].Discovered[192.168.1.230].ObscuredMAC"));
        // The obscured value is never emitted as a resolvable MAC.
        Assert.Null(Value(facts, "Device[_probe_].Discovered[192.168.1.60].MAC"));
        Assert.Equal(
            "walljm-macbook-intel.local",
            Value(facts, "Device[_probe_].Discovered[192.168.1.230].Hostname")
        );
        Assert.Equal("MacBookPro14,2", Value(facts, "Device[_probe_].Discovered[192.168.1.230].Model"));

        // Disconnected .99 must not appear.
        Assert.DoesNotContain(facts, f => f.Id.Contains("192.168.1.99", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collect_ReportFetchFails_ReturnsEmpty()
    {
        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal) ? NotFound() : Json("{}")
        );

        IReadOnlyList<Fact> facts = await Collect(handler, new FakeContext());

        Assert.Empty(facts);
    }

    [Fact]
    public async Task Collect_ReportHasNoDeviceId_ReturnsEmpty()
    {
        byte[] report = OnHubTestData.BuildReport(
            deviceId: "",
            OnHubTestData.NetworkState,
            (OnHubTestData.ApShowCommand, OnHubTestData.ApShowOutput)
        );
        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal)
                ? Gzipped(report)
                : Json(StatusJson)
        );

        IReadOnlyList<Fact> facts = await Collect(handler, new FakeContext());

        Assert.Empty(facts);
    }

    [Fact]
    public async Task Collect_StatusFails_StillEmitsIdentityAndStations()
    {
        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal)
                ? Gzipped(OnHubTestData.SampleReport())
                : NotFound()
        );
        FakeContext ctx = new();

        List<Fact> facts = (await Collect(handler, ctx)).ToList();

        Assert.NotNull(ctx.ResolvedFingerprints);
        Assert.Equal("Google", Value(facts, "Device[_probe_].Vendor"));
        // No status ⇒ no model / OS version.
        Assert.Null(Value(facts, "Device[_probe_].Hardware.SystemModel"));
        Assert.Null(Value(facts, "Device[_probe_].OS.Version"));
        // Stations still surface.
        Assert.Equal("00e0bf1fc40*", Value(facts, "Device[_probe_].Discovered[192.168.1.60].ObscuredMAC"));
    }

    [Fact]
    public async Task Collect_MissingAddress_ReturnsEmpty()
    {
        StubHandler handler = new(_ => NotFound());
        GoogleWifiCollector collector = new(new OnHubClient(new HttpClient(handler)));

        IReadOnlyList<Fact> facts = await collector.CollectAsync(
            new Target
            {
                Endpoint = "  ",
                CollectorType = "google-wifi",
            },
            new FakeContext(),
            CancellationToken.None
        );

        Assert.Empty(facts);
    }

    [Fact]
    public async Task Collect_EmitsRichClientAndApFacts()
    {
        const string networkState =
            """
            network_service_state {
              station_state_updates {
                station_state_update {
                  station_info {
                    station_id: "AA11"
                    connected: true
                    ip_addresses: "192.168.1.211"
                    wireless: true
                    wireless_interface: "wlan-5000mhz"
                    guest: false
                    oui: "f01898"
                    dns_sd_features {
                      key: "Nest-Audio-1294150e88618bcc369e24bf70d0c24a._googlecast._tcp.local"
                      value: "fn=Kitchen Audio"
                    }
                  }
                }
              }
            }
            """;
        const string apShow =
            """
            station_info {
              mac_address: "f018981fc40*"
              ipv4_addresses: "192.168.1.211"
            }
            """;
        const string iw =
            "Station f018981fc40* (on wlan-5000mhz)\n\tsignal:\t-52 dBm\n\ttx bitrate:\t390.0 MBit/s\n";
        const string meminfo = "MemTotal:         490860 kB\nMemFree:           84000 kB\n";
        const string lsb =
            "CHROMEOS_RELEASE_BOARD=gale\nCHROMEOS_RELEASE_NAME=Chrome OS\nCHROMEOS_RELEASE_DESCRIPTION=14150.376.32 stable gale\n";

        byte[] report = OnHubTestData.BuildReport(
            OnHubTestData.DeviceId,
            networkState,
            (OnHubTestData.ApShowCommand, apShow),
            ("/usr/sbin/iw dev wlan-5000mhz station dump", iw),
            ("/proc/meminfo", meminfo),
            ("/etc/lsb-release", lsb)
        );
        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal) ? Gzipped(report) : Json(StatusJson)
        );

        List<Fact> facts = (await Collect(handler, new FakeContext())).ToList();

        // ── Intrinsic client detail (mirrors Device[]; promoted server-side) ──
        Assert.Equal("Kitchen Audio", Value(facts, "Device[_probe_].Discovered[192.168.1.211].FriendlyName"));
        Assert.Equal("Nest-Audio", Value(facts, "Device[_probe_].Discovered[192.168.1.211].DeviceType"));
        Assert.Equal(
            "1294150e88618bcc369e24bf70d0c24a",
            Value(facts, "Device[_probe_].Discovered[192.168.1.211].CastId")
        );
        // Services are a list dimension (key = service type).
        Assert.Equal(
            "_googlecast._tcp",
            Value(facts, "Device[_probe_].Discovered[192.168.1.211].Service[_googlecast._tcp].Name")
        );

        // ── The sighting / link (stays on the observation) ──
        Assert.Equal("wireless", Value(facts, "Device[_probe_].Discovered[192.168.1.211].Link.Medium"));
        Assert.Equal("5GHz", Value(facts, "Device[_probe_].Discovered[192.168.1.211].Link.Band"));
        Assert.Equal(-52L, Long(facts, "Device[_probe_].Discovered[192.168.1.211].Link.SignalDbm"));
        Assert.Equal(
            390.0,
            facts.First(f => f.Id == "Device[_probe_].Discovered[192.168.1.211].Link.TxRateMbps").Value.AsDouble()
        );

        // ── AP facts (Tier 3) ──
        Assert.Equal(490860L * 1024, Long(facts, "Device[_probe_].Hardware.TotalMemBytes"));
        Assert.Equal("14150.376.32 stable gale", Value(facts, "Device[_probe_].OS.Build"));
        Assert.Equal("Chrome OS", Value(facts, "Device[_probe_].OS.Distro"));
        Assert.Equal("linux", Value(facts, "Device[_probe_].OS.Family"));
        // Boot time = now − uptime (123456s from StatusJson); assert it parses and is in the past.
        string? bootTime = Value(facts, "Device[_probe_].OS.BootTime");
        Assert.NotNull(bootTime);
        Assert.True(DateTimeOffset.Parse(bootTime, CultureInfo.InvariantCulture) < DateTimeOffset.UtcNow);
        Assert.Equal("192.168.1.1", Value(facts, "Device[_probe_].Interface[br-lan].IPv4"));
        Assert.Equal("70.106.253.205", Value(facts, "Device[_probe_].Interface[wan0].IPv4"));
    }

    [Fact]
    public async Task Collect_MeshPointStation_EmitsMeshDeviceType()
    {
        // A satellite Wifi point: listed in mesh_group.node_info (networkState/field 16's
        // shape), connected, Google's OUI, but no usable mDNS name or model — without the
        // mesh flag this would be dropped entirely.
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
                oui: "703acb"
                device_model: ""
              }
            }
            """;
        byte[] report = OnHubTestData.BuildReport(OnHubTestData.DeviceId, networkState);
        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal) ? Gzipped(report) : Json(StatusJson)
        );

        List<Fact> facts = (await Collect(handler, new FakeContext())).ToList();

        Assert.Equal("OnHub Mesh Point", Value(facts, "Device[_probe_].Discovered[192.168.1.215].DeviceType"));
        Assert.Null(Value(facts, "Device[_probe_].Discovered[192.168.1.215].Hostname")); // masked mDNS
    }

    [Fact]
    public async Task Collect_DhcpReservedStation_EmitsIsDhcpReserved()
    {
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
        byte[] report = OnHubTestData.BuildReport(OnHubTestData.DeviceId, networkState);
        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal) ? Gzipped(report) : Json(StatusJson)
        );

        List<Fact> facts = (await Collect(handler, new FakeContext())).ToList();

        Fact reserved = facts.First(f => f.Id == "Device[_probe_].Discovered[192.168.1.224].IsDhcpReserved");
        Assert.True(reserved.Value.AsBool());
        Assert.Equal("192.168.1.224", Value(facts, "Device[_probe_].Discovered[192.168.1.224].DhcpReservedIp"));

        // A non-reserved station gets no fact at all (not an explicit false).
        Assert.DoesNotContain(facts, f => f.Id.Contains("192.168.1.230", StringComparison.Ordinal) && f.Id.Contains("IsDhcpReserved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collect_WanDetail_EmitsConnectionTypeGatewayDnsAndSpeedTest()
    {
        // Shape verified against a live capture (docs/scratch/deep-dive-2.md §3).
        const string networkState =
            """
            network_service_state {
              wan_configuration {
                type: DHCP
              }
              wan_state {
                ip_address: "70.106.253.205"
                gateway_address: "70.106.253.1"
                link_speed_mbps: 1000
                primary_wan_interface: "wan0"
              }
              wan_name_servers {
                dns_server: "71.252.0.12"
                dns_server: "71.242.0.12"
              }
            }
            infra_state {
              isp_configuration {
                isp_type: ISP_NONE
              }
              wan_speed_test_results {
                date_time_seconds_since_epoch: 1783975053
                download_speed_bytes_per_second: 12345678
                upload_speed_bytes_per_second: 2345678
                total_bytes_downloaded: 999999999
                total_bytes_uploaded: 888888888
              }
            }
            """;
        byte[] report = OnHubTestData.BuildReport(OnHubTestData.DeviceId, networkState);
        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal) ? Gzipped(report) : Json(StatusJson)
        );

        List<Fact> facts = (await Collect(handler, new FakeContext())).ToList();

        Assert.Equal("DHCP", Value(facts, "Device[_probe_].Interface[wan0].ConnectionType"));
        Assert.Equal("70.106.253.1", Value(facts, "Device[_probe_].Interface[wan0].Gateway"));
        Assert.Equal("ISP_NONE", Value(facts, "Device[_probe_].Interface[wan0].IspType"));

        Assert.Equal("71.252.0.12", Value(facts, "Device[_probe_].Network.DNS[0]"));
        Assert.Equal("71.242.0.12", Value(facts, "Device[_probe_].Network.DNS[1]"));

        Assert.Equal(12345678L, Long(facts, "Device[_probe_].Network.WanSpeedTest.DownloadBytesPerSec"));
        Assert.Equal(2345678L, Long(facts, "Device[_probe_].Network.WanSpeedTest.UploadBytesPerSec"));
        Assert.Equal(999999999L, Long(facts, "Device[_probe_].Network.WanSpeedTest.TotalDownloadedBytes"));
        Assert.Equal(888888888L, Long(facts, "Device[_probe_].Network.WanSpeedTest.TotalUploadedBytes"));
        Fact testedAt = facts.First(f => f.Id == "Device[_probe_].Network.WanSpeedTest.At");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1783975053), testedAt.Value.AsDateTimeOffset());
    }

    [Fact]
    public async Task Collect_NoNetworkServiceOrInfraState_EmitsNoWanFacts()
    {
        const string networkState =
            """
            station_state_update {
              station_info {
                station_id: "EE55"
                connected: true
                ip_addresses: "192.168.1.92"
              }
            }
            """;
        byte[] report = OnHubTestData.BuildReport(OnHubTestData.DeviceId, networkState);
        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal) ? Gzipped(report) : Json(StatusJson)
        );

        List<Fact> facts = (await Collect(handler, new FakeContext())).ToList();

        Assert.DoesNotContain(facts, f => f.Id.Contains("ConnectionType", StringComparison.Ordinal));
        Assert.DoesNotContain(facts, f => f.Id.Contains("WanSpeedTest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collect_HostapdLog_EmitsLastActiveAndRoamingLinkFacts()
    {
        const string networkState =
            """
            station_state_update {
              station_info {
                station_id: "EE55"
                connected: true
                ip_addresses: "192.168.1.92"
                oui: "50579c"
              }
            }
            """;
        const string apShowOutput =
            """
            station_info {
              mac_address: "aabbccdd11f*"
              station_id: "****************************************************************"
              ipv4_addresses: "192.168.1.92"
              wireless_interface: ""
            }
            """;
        DiagnosticReport report = DiagnosticReport.Parser.ParseFrom(
            OnHubTestData.BuildReport(OnHubTestData.DeviceId, networkState, (OnHubTestData.ApShowCommand, apShowOutput))
        );
        report.Files.Add(
            new JMW.Discovery.Agent.Collection.Device.OnHub.Proto.File
            {
                Path = "/var/log/messages",
                Content = ByteString.CopyFromUtf8(
                    """
                    2026-07-13T20:11:41.300311Z INFO hostapd[2513]: wlan-2400mhz: STA aabbccdd11f*      IEEE 802.11: Station aabbccdd11f*      has been active 0s ago
                    2026-07-13T20:30:02.515930Z INFO hostapd[2513]: wlan-2400mhz: STA aabbccdd11f*      IAPP: Received IAPP ADD-notify (seq# 0) from 192.168.1.217:3517 (STA not found)
                    """
                ),
            }
        );
        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal) ? Gzipped(report.ToByteArray()) : Json(StatusJson)
        );

        List<Fact> facts = (await Collect(handler, new FakeContext())).ToList();

        Assert.Equal(
            DateTimeOffset.Parse("2026-07-13T20:11:41.300311Z"),
            facts.First(f => f.Id == "Device[_probe_].Discovered[192.168.1.92].Link.LastActiveAt").Value.AsDateTimeOffset()
        );
        Assert.Equal("wlan-2400mhz", Value(facts, "Device[_probe_].Discovered[192.168.1.92].Link.LastActiveInterface"));
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-13T20:30:02.515930Z"),
            facts.First(f => f.Id == "Device[_probe_].Discovered[192.168.1.92].Link.LastRoamingAt").Value.AsDateTimeOffset()
        );
        Assert.Equal("192.168.1.217", Value(facts, "Device[_probe_].Discovered[192.168.1.92].Link.LastRoamingApIp"));
    }

    [Fact]
    public async Task Collect_NestedReport_MergesSatelliteStationsNotInOuterReport()
    {
        // nestedReport (field 15) is a complete DiagnosticReport from a different physical
        // mesh unit — verified against a live capture. Its stations should merge into the
        // same Discovered[] list, additively, for IPs the outer report didn't already cover.
        const string nestedNetworkState =
            """
            station_state_update {
              station_info {
                station_id: "NESTEDSTATIONID0000000000000000000000000000000000000000000001"
                mdns_name: "satellite-only-device.local"
                connected: true
                ip_addresses: "192.168.1.99"
              }
            }
            """;
        byte[] nestedBytes = OnHubTestData.BuildReport("NESTEDDEVICEID0000000000000000", nestedNetworkState);

        DiagnosticReport outer = DiagnosticReport.Parser.ParseFrom(OnHubTestData.SampleReport());
        outer.NestedReport = ByteString.CopyFrom(OnHubTestData.Gzip(nestedBytes));

        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal)
                ? Gzipped(outer.ToByteArray())
                : Json(StatusJson)
        );

        List<Fact> facts = (await Collect(handler, new FakeContext())).ToList();

        // Outer report's own stations still present.
        Assert.Equal("00e0bf1fc40*", Value(facts, "Device[_probe_].Discovered[192.168.1.60].ObscuredMAC"));
        // Satellite-only station merged in.
        Assert.Equal(
            "satellite-only-device.local",
            Value(facts, "Device[_probe_].Discovered[192.168.1.99].Hostname")
        );
    }

    [Fact]
    public async Task Collect_NestedReport_OuterStationWinsOnIpOverlap()
    {
        // Same IP (192.168.1.230) in both outer and nested reports, with different data —
        // the outer (primary) report's version must win; the nested one is ignored for it.
        const string nestedNetworkState =
            """
            station_state_update {
              station_info {
                station_id: "NESTEDSTATIONID0000000000000000000000000000000000000000000002"
                mdns_name: "wrong-satellite-name.local"
                connected: true
                ip_addresses: "192.168.1.230"
              }
            }
            """;
        byte[] nestedBytes = OnHubTestData.BuildReport("NESTEDDEVICEID0000000000000000", nestedNetworkState);

        DiagnosticReport outer = DiagnosticReport.Parser.ParseFrom(OnHubTestData.SampleReport());
        outer.NestedReport = ByteString.CopyFrom(OnHubTestData.Gzip(nestedBytes));

        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal)
                ? Gzipped(outer.ToByteArray())
                : Json(StatusJson)
        );

        List<Fact> facts = (await Collect(handler, new FakeContext())).ToList();

        Assert.Equal(
            "walljm-macbook-intel.local",
            Value(facts, "Device[_probe_].Discovered[192.168.1.230].Hostname")
        );
    }

    [Fact]
    public async Task Collect_CorruptNestedReport_DegradesGracefullyToOuterStationsOnly()
    {
        DiagnosticReport outer = DiagnosticReport.Parser.ParseFrom(OnHubTestData.SampleReport());
        outer.NestedReport = ByteString.CopyFrom([0x1F, 0x8B, 0x08, 0x00, 0xFF, 0xFF, 0xFF]); // corrupt gzip

        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal)
                ? Gzipped(outer.ToByteArray())
                : Json(StatusJson)
        );

        List<Fact> facts = (await Collect(handler, new FakeContext())).ToList();

        // Collection still succeeds with the outer report's own stations.
        Assert.Equal("00e0bf1fc40*", Value(facts, "Device[_probe_].Discovered[192.168.1.60].ObscuredMAC"));
    }

    [Fact]
    public async Task Collect_UpnpOnlyStation_EmitsVendorAndModelWithNoMdns()
    {
        // A printer with no dns_sd_features at all — UPnP is the only identity source.
        const string networkState =
            """
            station_state_update {
              station_info {
                station_id: "EE55"
                connected: true
                ip_addresses: "192.168.1.92"
                wireless: false
                oui: "50579c"
                upnp_attribute {
                  key: "friendlyName"
                  value: "EPSONP900"
                }
                upnp_attribute {
                  key: "manufacturer"
                  value: "EPSON"
                }
                upnp_attribute {
                  key: "modelName"
                  value: "SC-P900 Series"
                }
              }
            }
            """;
        byte[] report = OnHubTestData.BuildReport(OnHubTestData.DeviceId, networkState);
        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal) ? Gzipped(report) : Json(StatusJson)
        );

        List<Fact> facts = (await Collect(handler, new FakeContext())).ToList();

        Assert.Equal("EPSONP900", Value(facts, "Device[_probe_].Discovered[192.168.1.92].FriendlyName"));
        Assert.Equal("EPSON", Value(facts, "Device[_probe_].Discovered[192.168.1.92].Vendor"));
        Assert.Equal("SC-P900 Series", Value(facts, "Device[_probe_].Discovered[192.168.1.92].Model"));
        Assert.Null(Value(facts, "Device[_probe_].Discovered[192.168.1.92].Hostname"));
    }

    [Fact]
    public async Task Collect_ParsesApInterfacesFromIpAddr()
    {
        // When the report carries `ip -s -d addr`, the AP's real interface inventory
        // replaces the two synthesized (br-lan/wan0) interfaces.
        const string ipAddr =
            """
            1: lo: <LOOPBACK,UP,LOWER_UP> mtu 65536 qdisc noqueue state UNKNOWN group default qlen 1000
                link/loopback 00000079357*      brd 00000079357*      promiscuity 0
            4: wan0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc mq state UP group default qlen 1000
                link/ether 703acb1f8f8*      brd ffffff551b5*      promiscuity 0
                inet 173.67.196.15/24 brd 173.67.196.255 scope global wan0
            5: lan0: <NO-CARRIER,BROADCAST,MULTICAST,UP> mtu 1500 qdisc mq master br-lan state DOWN group default qlen 1000
                link/ether 703acb70d06*      brd ffffff551b5*      promiscuity 1
                bridge_slave state disabled priority 32 cost 100
            8: br-lan: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc noqueue state UP group default qlen 1000
                link/ether 703acb70d06*      brd ffffff551b5*      promiscuity 0
                bridge forward_delay 300 hello_time 200 max_age 600
                inet 192.168.1.1/24 scope global br-lan
            """;
        const string ethtoolWan =
            "Settings for wan0:\n\tSpeed: 1000Mb/s\n\tDuplex: Full\n\tLink detected: yes\n";

        byte[] report = OnHubTestData.BuildReport(
            OnHubTestData.DeviceId,
            networkState: "",
            ("/bin/ip -s -d addr", ipAddr),
            ("/usr/sbin/ethtool wan0", ethtoolWan)
        );
        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal) ? Gzipped(report) : Json(StatusJson)
        );

        List<Fact> facts = (await Collect(handler, new FakeContext())).ToList();

        // All four interfaces present (not just the two synthetic ones).
        Assert.Equal("br-lan", Value(facts, "Device[_probe_].Interface[br-lan].Name"));
        Assert.Equal("192.168.1.1", Value(facts, "Device[_probe_].Interface[br-lan].IPv4"));
        Assert.Equal("bridge", Value(facts, "Device[_probe_].Interface[br-lan].Type"));
        Assert.Equal(1500L, Long(facts, "Device[_probe_].Interface[br-lan].MTU"));

        // Prefix length is captured separately from the (deliberately bare) IPv4 fact — the
        // Subnets page needs it to synthesize a CIDR for an isolated interface (e.g. a guest
        // network) with no other peer already covering its subnet.
        Assert.Equal(24L, Long(facts, "Device[_probe_].Interface[br-lan].IPv4PrefixLength"));
        Assert.Equal(24L, Long(facts, "Device[_probe_].Interface[wan0].IPv4PrefixLength"));

        Assert.Equal("173.67.196.15", Value(facts, "Device[_probe_].Interface[wan0].IPv4"));
        Assert.Equal("loopback", Value(facts, "Device[_probe_].Interface[lo].Type"));
        Assert.Equal(65536L, Long(facts, "Device[_probe_].Interface[lo].MTU"));

        // Obscured MACs are captured raw for server-side reconstruction; loopback has none.
        Assert.Equal("703acb1f8f8*", Value(facts, "Device[_probe_].Interface[wan0].ObscuredMAC"));
        Assert.Equal("703acb70d06*", Value(facts, "Device[_probe_].Interface[br-lan].ObscuredMAC"));
        Assert.Null(Value(facts, "Device[_probe_].Interface[lo].ObscuredMAC"));

        // ethtool link speed/duplex merged onto wan0.
        Assert.Equal(1000L * 1_000_000, Long(facts, "Device[_probe_].Interface[wan0].SpeedBps"));
        Assert.Equal("Full", Value(facts, "Device[_probe_].Interface[wan0].Duplex"));

        // The no-carrier slave is down; every parsed interface has a Name fact.
        Assert.Equal("lan0", Value(facts, "Device[_probe_].Interface[lan0].Name"));
        int interfaceNameFacts = facts.Count(f
            => f.Id.StartsWith("Device[_probe_].Interface[", StringComparison.Ordinal)
         && f.Id.EndsWith("].Name", StringComparison.Ordinal)
        );
        Assert.Equal(4, interfaceNameFacts);
    }

    [Fact]
    public async Task Collect_ParsesFilesystemsAndDisksFromFindmnt()
    {
        const string findmnt =
            """
            TARGET                           SOURCE                        FSTYPE   OPTIONS
            /                                /dev/dm-0                     ext2     ro,relatime
            |-/proc                          proc                          proc     ro,nosuid
            |-/mnt/stateful_partition        /dev/mmcblk0p1                ext4     rw,nosuid
            |-/usr/share/oem                 /dev/mmcblk0p8                ext4     ro,nosuid
            """;

        byte[] report = OnHubTestData.BuildReport(
            OnHubTestData.DeviceId,
            networkState: "",
            ("/bin/findmnt", findmnt)
        );
        StubHandler handler = new(url =>
            url.Contains("/diagnostic-report", StringComparison.Ordinal) ? Gzipped(report) : Json(StatusJson)
        );

        List<Fact> facts = (await Collect(handler, new FakeContext())).ToList();

        // Device-backed filesystems (mount + type); pseudo /proc dropped.
        Assert.Equal("ext2", Value(facts, "Device[_probe_].Filesystem[/].FsType"));
        Assert.Equal("ext4", Value(facts, "Device[_probe_].Filesystem[/mnt/stateful_partition].FsType"));
        Assert.Null(Value(facts, "Device[_probe_].Filesystem[/proc].FsType"));

        // Distinct parent disks (dm-0 kept whole; mmcblk0p1/p8 → mmcblk0).
        Assert.Equal("dm-0", Value(facts, "Device[_probe_].Disk[dm-0].Name"));
        Assert.Equal("mmcblk0", Value(facts, "Device[_probe_].Disk[mmcblk0].Name"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static long? Long(IEnumerable<Fact> facts, string id) =>
        facts.FirstOrDefault(fact => fact.Id == id) is var f ? f.Value.AsLong() : null;

    // Mirrors the real /api/v1/status shape: uptime/countryCode nest under "system".
    private const string StatusJson =
        """
        {
          "system": { "hardwareId": "GALE C2I-A2I-A3C-A4I-E5K", "modelId": "ACc3d", "uptime": 123456, "countryCode": "us" },
          "software": { "softwareVersion": "14150.376.32" },
          "wan": { "localIpAddress": "70.106.253.205", "gatewayIpAddress": "70.106.253.1" },
          "setupState": "GWIFI_OOBE_COMPLETE"
        }
        """;

    private static Task<IReadOnlyList<Fact>> Collect(StubHandler handler, FakeContext ctx)
    {
        GoogleWifiCollector collector = new(new OnHubClient(new HttpClient(handler)));
        return collector.CollectAsync(
            new Target
            {
                Endpoint = "192.168.1.1",
                CollectorType = "google-wifi",
            },
            ctx,
            CancellationToken.None
        );
    }

    private static (HttpStatusCode, byte[], string) SampleRoute(string url) =>
        url.Contains("/diagnostic-report", StringComparison.Ordinal)
            ? Gzipped(OnHubTestData.SampleReport())
            : url.Contains("/status", StringComparison.Ordinal)
                ? Json(StatusJson)
                : NotFound();

    private static (HttpStatusCode, byte[], string) Gzipped(byte[] report) =>
        (HttpStatusCode.OK, OnHubTestData.Gzip(report), "application/octet-stream");

    private static (HttpStatusCode, byte[], string) Json(string json) =>
        (HttpStatusCode.OK, Encoding.UTF8.GetBytes(json), "application/json");

    private static (HttpStatusCode, byte[], string) NotFound() =>
        (HttpStatusCode.NotFound, [], "text/plain");

    private static string? Value(IEnumerable<Fact> facts, string id) =>
        facts.FirstOrDefault(fact => fact.Id == id) is { } f ? f.Value.AsString() : null;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode Status, byte[] Body, string ContentType)> _route;

        public StubHandler(Func<string, (HttpStatusCode, byte[], string)> route)
        {
            _route = route;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            (HttpStatusCode status, byte[] body, string contentType) = _route(request.RequestUri!.AbsoluteUri);
            HttpResponseMessage resp = new(status)
            {
                Content = new ByteArrayContent(body),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return Task.FromResult(resp);
        }
    }

    private sealed class FakeContext : ICollectionContext
    {
        public string AgentId => "agent-1";
        public IReadOnlyList<Fingerprint>? ResolvedFingerprints { get; private set; }
        public DeviceIdentity? ResolvedIdentity { get; private set; }

        public Task<string> RegisterProbeAsync(DeviceIdentity identity, CancellationToken ct)
        {
            ResolvedFingerprints = identity.Fingerprints;
            ResolvedIdentity = identity;
            return Task.FromResult("_probe_");
        }
    }
}