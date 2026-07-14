using JMW.Discovery.Agent.Collection.Device.OnHub;
using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;

namespace JMW.Discovery.UnitTests.Agent.OnHub;

/// <summary>
/// Covers the Tier 1/2/4 per-client enrichment: mDNS-derived friendly name / device
/// type / model / services, wired-vs-wireless + band + guest, iw wireless telemetry,
/// and ARP-sourced neighbours.
/// </summary>
public sealed class OnHubClientDetailTests
{
    private const string RichNetworkState =
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
                dns_sd_features {
                  key: "Nest-Audio-1294150e88618bcc369e24bf70d0c24a._googlecast._tcp.local"
                  value: "md=Google Nest Audio"
                }
                dns_sd_features {
                  key: "Nest-Audio-1294150e88618bcc369e24bf70d0c24a._googlecast._tcp.local"
                  value: "ca=199172"
                }
                dns_sd_features {
                  key: "Nest-Audio-1294150e88618bcc369e24bf70d0c24a._googlecast._tcp.local"
                  value: "st=0"
                }
                dns_sd_features {
                  key: "Nest-Audio-1294150e88618bcc369e24bf70d0c24a._googlecast._tcp.local"
                  value: "rs=Casting: Netflix"
                }
              }
            }
            station_state_update {
              station_info {
                mdns_name: "walljm-macbook-intel.local"
                connected: true
                ip_addresses: "192.168.1.230"
                wireless: false
                oui: "f01898"
                dns_sd_features {
                  key: "walljm-macbook-intel._device-info._tcp.local"
                  value: "model=MacBookPro14,2"
                }
                dns_sd_features {
                  key: "walljm-macbook-m3._airplay._tcp.local"
                  value: "deviceid=x"
                }
              }
            }
            station_state_update {
              station_info {
                station_id: "CC33"
                connected: true
                ip_addresses: "192.168.1.55"
                wireless: true
                wireless_interface: "guest-2400mhz"
                guest: true
                oui: "703acb"
              }
            }
            station_state_update {
              station_info {
                station_id: "DD44"
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
                upnp_attribute {
                  key: "modelNumber"
                  value: "SC-P900 Series"
                }
              }
            }
          }
        }
        """;

    private const string RichApShow =
        """
        station_info {
          mac_address: "f018981fc40*"
          ipv4_addresses: "192.168.1.211"
        }
        station_info {
          mac_address: "f01898ab2cd*"
          ipv4_addresses: "192.168.1.230"
        }
        station_info {
          mac_address: "703acb112ab*"
          ipv4_addresses: "192.168.1.55"
        }
        station_info {
          mac_address: "50579c68d1d*"
          ipv4_addresses: "192.168.1.92"
        }
        """;

    // Real firmware serves tab-separated "label:\tvalue" lines.
    private const string IwStationDump =
        "Station f018981fc40* (on wlan-5000mhz)\n"
      + "\tinactive time:\t100 ms\n"
      + "\trx bytes:\t12345678\n"
      + "\ttx bytes:\t87654321\n"
      + "\tsignal:\t-52 dBm\n"
      + "\ttx bitrate:\t390.0 MBit/s VHT-MCS 4\n"
      + "\trx bitrate:\t433.3 MBit/s VHT-MCS 9 short GI\n"
      + "\tconnected time:\t3600 seconds\n";

    private const string ArpTable =
        "IP address       HW type     Flags       HW address            Mask     Device\n"
      + "192.168.1.5      0x1         0x2         001122334ab*          *        br-lan\n"
      + "192.168.1.6      0x1         0x0         deadbeef123*          *        br-lan\n";

    private static IReadOnlyList<OnHubStation> Extract() =>
        OnHubStations.Extract(
            DiagnosticReport.Parser.ParseFrom(
                OnHubTestData.BuildReport(
                    "ADEC2AD4",
                    RichNetworkState,
                    (OnHubTestData.ApShowCommand, RichApShow),
                    ("/usr/sbin/iw dev wlan-5000mhz station dump", IwStationDump),
                    ("/proc/net/arp", ArpTable)
                )
            )
        );

    private static OnHubStation ByIp(string ip) =>
        Extract().Single(s => s.Ip == ip);

    [Fact]
    public void NestAudio_mDnsTypeNameServicesAndTelemetry()
    {
        OnHubStation s = ByIp("192.168.1.211");

        Assert.Equal("f018981fc40*", s.Mac);
        Assert.Equal("Kitchen Audio", s.FriendlyName);
        Assert.Equal("Nest-Audio", s.DeviceType);
        Assert.Equal("Google Nest Audio", s.Model);
        // Stable Cast device id = the trailing hex token of the _googlecast instance.
        Assert.Equal("1294150e88618bcc369e24bf70d0c24a", s.CastId);
        // Raw _googlecast TXT values — captured opaque, not decoded (undocumented format).
        Assert.Equal("199172", s.CastCapabilities);
        Assert.Equal("0", s.CastStatus);
        Assert.Equal("Casting: Netflix", s.CastRunningApp);
        Assert.Equal(["_googlecast._tcp"], s.ServiceTypes);
        Assert.Equal("wireless", s.ConnectionMedium);
        Assert.Equal("5GHz", s.Band);
        Assert.False(s.Guest);
        Assert.Equal("f01898", s.Oui); // raw OUI, not a vendor name

        // Wireless telemetry joined from the iw station dump by obscured MAC.
        Assert.Equal(-52L, s.SignalDbm);
        Assert.Equal(390.0, s.TxRateMbps);
        Assert.Equal(433.3, s.RxRateMbps);
        Assert.Equal(12345678L, s.RxBytes);
        Assert.Equal(87654321L, s.TxBytes);
        Assert.Equal(3600L, s.ConnectedSeconds);
    }

    [Fact]
    public void Macbook_WiredWithModelAndMultipleServices()
    {
        OnHubStation s = ByIp("192.168.1.230");

        Assert.Equal("walljm-macbook-intel.local", s.Hostname);
        Assert.Equal("MacBookPro14,2", s.Model);
        Assert.Equal(["_airplay._tcp", "_device-info._tcp"], s.ServiceTypes); // sorted, distinct
        Assert.Equal("apple-device", s.DeviceType); // classified from the _airplay service
        Assert.Equal("walljm-macbook-m3", s.FriendlyName); // from the _airplay instance label
        Assert.Equal("wired", s.ConnectionMedium);
        Assert.Null(s.Band);
        Assert.Equal("f01898", s.Oui);
    }

    [Fact]
    public void RaopAirplay_FriendlyNameFromKey_AndMdIsNotAModel()
    {
        // A device advertising only AirPlay/RAOP: the friendly name lives in the key
        // instance (after the "<deviceid>@"), and _raop "md=0,1,2" is codec metadata,
        // NOT a model — it must never be stored as the model. Unicode in the name
        // (curly apostrophe, octal-escaped in the wire text) must decode cleanly.
        const string networkState =
            """
            station_state_update {
              station_info {
                connected: true
                ip_addresses: "192.168.1.219"
                oui: "36db0e"
                dns_sd_features {
                  key: "WonderWoman\342\200\231s MacBook Pro._airplay._tcp.local"
                  value: "model=Mac15,3"
                }
                dns_sd_features {
                  key: "5E706461E0E2@WonderWoman\342\200\231s MacBook Pro._raop._tcp.local"
                  value: "md=0,1,2"
                }
              }
            }
            """;
        byte[] report = OnHubTestData.BuildReport("ABCD1234", networkState);
        OnHubStation s = OnHubStations.Extract(DiagnosticReport.Parser.ParseFrom(report))
            .Single(x => x.Ip == "192.168.1.219");

        Assert.Equal("WonderWoman’s MacBook Pro", s.FriendlyName);
        Assert.Equal("Mac15,3", s.Model); // model= wins; md=0,1,2 is ignored
        Assert.Equal("apple-device", s.DeviceType);
        Assert.Equal(["_airplay._tcp", "_raop._tcp"], s.ServiceTypes);
        // No cast id: the AirPlay/RAOP "<hex>@" prefix is a randomized/synthetic id,
        // never a stable Cast device id (only _googlecast yields one).
        Assert.Null(s.CastId);
    }

    [Fact]
    public void RaopOnly_MdMetadata_NeverBecomesModel()
    {
        // RAOP-only speaker group: no model= anywhere, only md=0,1,2. Model stays null.
        const string networkState =
            """
            station_state_update {
              station_info {
                connected: true
                ip_addresses: "192.168.1.237"
                oui: "cccc0c"
                dns_sd_features {
                  key: "CCCC0CF9753B@Great Room Audio+._raop._tcp.local"
                  value: "md=0,1,2"
                }
              }
            }
            """;
        byte[] report = OnHubTestData.BuildReport("ABCD1234", networkState);
        OnHubStation s = OnHubStations.Extract(DiagnosticReport.Parser.ParseFrom(report))
            .Single(x => x.Ip == "192.168.1.237");

        Assert.Null(s.Model);
        Assert.Equal("Great Room Audio", s.FriendlyName); // '@'-prefix stripped, trailing '+' trimmed
    }

    [Fact]
    public void RaopOnly_CaStRsValues_NeverReadAsCastState()
    {
        // ca=/st=/rs= are gated to _googlecast — a coincidentally-matching value under a
        // different service (RAOP here) must never be misread as Cast capability/status.
        const string networkState =
            """
            station_state_update {
              station_info {
                connected: true
                ip_addresses: "192.168.1.238"
                oui: "cccc0d"
                dns_sd_features {
                  key: "CCCC0DF9753C@Some Speaker._raop._tcp.local"
                  value: "st=notCastState"
                }
              }
            }
            """;
        byte[] report = OnHubTestData.BuildReport("ABCD1234", networkState);
        OnHubStation s = OnHubStations.Extract(DiagnosticReport.Parser.ParseFrom(report))
            .Single(x => x.Ip == "192.168.1.238");

        Assert.Null(s.CastCapabilities);
        Assert.Null(s.CastStatus);
        Assert.Null(s.CastRunningApp);
    }

    [Fact]
    public void Printer_UpnpAttributesGiveManufacturerAndModel_NoMdns()
    {
        // No dns_sd_features at all for this station — UPnP is the only identity source.
        OnHubStation s = ByIp("192.168.1.92");

        Assert.Equal("50579c68d1d*", s.Mac);
        Assert.Equal("EPSONP900", s.FriendlyName);
        Assert.Equal("EPSON", s.Manufacturer);
        // modelName == modelNumber here, so the combining rule doesn't duplicate it.
        Assert.Equal("SC-P900 Series", s.Model);
        Assert.Null(s.Hostname); // no mdns_name
    }

    [Fact]
    public void Upnp_ModelNameAndNumberDiffer_CombinesBoth()
    {
        const string networkState =
            """
            station_state_update {
              station_info {
                connected: true
                ip_addresses: "192.168.1.93"
                oui: "50579c"
                upnp_attribute {
                  key: "modelName"
                  value: "Widget"
                }
                upnp_attribute {
                  key: "modelNumber"
                  value: "X100"
                }
              }
            }
            """;
        byte[] report = OnHubTestData.BuildReport("ABCD1234", networkState);
        OnHubStation s = OnHubStations.Extract(DiagnosticReport.Parser.ParseFrom(report))
            .Single(x => x.Ip == "192.168.1.93");

        Assert.Equal("Widget X100", s.Model);
    }

    [Fact]
    public void GuestStation_GuestFlagAndBand()
    {
        OnHubStation s = ByIp("192.168.1.55");

        Assert.True(s.Guest);
        Assert.Equal("2.4GHz", s.Band);
        Assert.Equal("wireless", s.ConnectionMedium);
    }

    [Fact]
    public void ArpReachableNeighbour_SurfacedAsStation()
    {
        IReadOnlyList<OnHubStation> stations = Extract();

        OnHubStation? wired = stations.FirstOrDefault(s => s.Ip == "192.168.1.5");
        Assert.NotNull(wired);
        Assert.Equal("001122334ab*", wired.Mac);

        // Incomplete ARP entry (flags 0x0) is not surfaced.
        Assert.DoesNotContain(stations, s => s.Ip == "192.168.1.6");
    }
}