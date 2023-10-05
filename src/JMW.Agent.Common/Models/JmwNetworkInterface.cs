using System.Net.NetworkInformation;

namespace JMW.Agent.Common.Models;

public class JmwNetworkInterface
{
    public int? Ipv6LoopbackInterfaceIndex { get; set; }
    public int? LoopbackInterfaceIndex { get; set; }

    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public PhysicalAddress? PhysicalAddress { get; set; }

    /// <summary>
    /// The current operational state of the network connection.
    /// </summary>
    public System.Net.NetworkInformation.OperationalStatus? OperationalStatus { get; set; }

    /// <summary>
    /// The speed of the interface in bits per second as reported by the interface.
    /// </summary>
    public long? Speed { get; set; }

    /// <summary>
    /// A bool value that indicates whether the network interface is set to only receive data packets.
    /// </summary>
    public bool? IsReceiveOnly { get; set; }

    /// <summary>
    /// A bool value that indicates whether this network interface is enabled to receive multicast packets.
    /// </summary>
    public bool? SupportsMulticast { get; set; }

    public string? NetworkInterfaceType { get; set; }
    public string? NetworkInterfaceTypeDescription { get; set; }

    public JmwIpInterfaceStatistics? IpInterfaceStatistics { get; set; }
    public JmwIpInterfaceStatistics? Ipv4InterfaceStatistics { get; set; }
    public JmwIpInterfaceProperties? IpInterfaceProperties { get; set; }
}
