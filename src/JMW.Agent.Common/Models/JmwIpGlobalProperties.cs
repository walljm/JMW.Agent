using System.Net.NetworkInformation;

namespace JMW.Agent.Common.Models;

public class JmwIpGlobalProperties
{
    /// <summary>
    /// Gets the Dynamic Host Configuration Protocol (DHCP) scope name.
    /// </summary>
    public string? DhcpScopeName { get; set; }

    /// <summary>
    /// Gets the domain in which the local computer is registered.
    /// </summary>
    public string? DomainName { get; set; }

    /// <summary>
    /// Gets the host name for the local computer.
    /// </summary>
    public string? HostName { get; set; }

    /// <summary>
    /// Gets a bool value that specifies whether the local computer is acting as a Windows Internet Name Service (WINS) proxy.
    /// </summary>
    public bool? IsWinsProxy { get; set; }

    /// <summary>
    /// Gets the Network Basic Input/Output System (NetBIOS) node type of the local computer.
    /// </summary>
    public NetBiosNodeType? NodeType { get; set; }
}