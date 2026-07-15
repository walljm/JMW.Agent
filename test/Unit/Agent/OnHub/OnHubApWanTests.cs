using JMW.Discovery.Agent.Collection.Device.OnHub;

namespace JMW.Discovery.UnitTests.Agent.OnHub;

public sealed class OnHubApWanTests
{
    // Shape verified against a live capture (docs/scratch/deep-dive-2.md §3): wan_state/
    // wan_configuration/wan_name_servers nest under network_service_state; isp_configuration
    // and wan_speed_test_results nest under the sibling infra_state.
    private const string NetworkState =
        """
        network_service_state {
          wan_configuration {
            type: DHCP
            static_configuration {
              ip_address: ""
              netmask: ""
              gateway: ""
            }
            pppoe_configuration {
              username: ""
            }
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

    [Fact]
    public void Extract_RealShape_ParsesConnectionGatewayDnsAndSpeedTest()
    {
        OnHubWanDetail? wan = OnHubApWan.Extract(NetworkState);

        Assert.NotNull(wan);
        Assert.Equal("DHCP", wan.ConnectionType);
        Assert.Equal("70.106.253.1", wan.Gateway);
        Assert.Equal("wan0", wan.PrimaryInterface);
        Assert.Equal("ISP_NONE", wan.IspType);
        Assert.Equal(["71.252.0.12", "71.242.0.12"], wan.DnsServers);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1783975053), wan.SpeedTestAt);
        Assert.Equal(12345678L, wan.SpeedTestDownloadBytesPerSec);
        Assert.Equal(2345678L, wan.SpeedTestUploadBytesPerSec);
        Assert.Equal(999999999L, wan.SpeedTestTotalDownloadedBytes);
        Assert.Equal(888888888L, wan.SpeedTestTotalUploadedBytes);

        // Empty static/pppoe blocks (this household uses DHCP) parse as null, not "".
        Assert.Null(wan.StaticIpAddress);
        Assert.Null(wan.PppoeUsername);
    }

    [Fact]
    public void Extract_MissingBothBlocks_ReturnsNull()
    {
        Assert.Null(OnHubApWan.Extract("station_state_update { station_info { connected: true } }"));
    }

    [Fact]
    public void Extract_EmptyNetworkState_ReturnsNull()
    {
        Assert.Null(OnHubApWan.Extract(""));
    }

    [Fact]
    public void Extract_NoSpeedTestResults_SpeedTestFieldsNull()
    {
        const string networkState =
            """
            network_service_state {
              wan_state {
                gateway_address: "192.168.1.1"
                primary_wan_interface: "wan0"
              }
            }
            """;

        OnHubWanDetail? wan = OnHubApWan.Extract(networkState);

        Assert.NotNull(wan);
        Assert.Equal("192.168.1.1", wan.Gateway);
        Assert.Null(wan.SpeedTestAt);
        Assert.Null(wan.SpeedTestDownloadBytesPerSec);
        Assert.Empty(wan.DnsServers);
    }
}