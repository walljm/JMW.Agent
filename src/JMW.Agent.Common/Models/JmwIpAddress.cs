using System.Net;
using System.Net.Sockets;

namespace JMW.Agent.Common.Models;

public class JmwIpAddress
{
    public JmwIpAddress()
    {
    }

    public JmwIpAddress(string ipString) : this(IPAddress.Parse(ipString))
    {
    }

    public JmwIpAddress(IPAddress ip)
    {
        this.Address = ip.ToString();
        this.AddressFamily = ip.AddressFamily;
        this.IsIPv6Multicast = ip.IsIPv6Multicast;
        this.IsIPv6LinkLocal = ip.IsIPv6LinkLocal;
        this.IsIPv6SiteLocal = ip.IsIPv6SiteLocal;
        this.IsIPv6Teredo = ip.IsIPv6Teredo;
        this.IsIPv6UniqueLocal = ip.IsIPv6UniqueLocal;
        this.IsIPv4MappedToIPv6 = ip.IsIPv4MappedToIPv6;
    }

    public string? Address { get; set; }

    public AddressFamily AddressFamily { get; set; }

    /// <devdoc>
    ///   <para>
    ///     Determines if an address is an IPv6 Multicast address
    ///   </para>
    /// </devdoc>
    public bool IsIPv6Multicast { get; set; }

    /// <devdoc>
    ///   <para>
    ///     Determines if an address is an IPv6 Link Local address
    ///   </para>
    /// </devdoc>
    public bool IsIPv6LinkLocal { get; set; }

    /// <devdoc>
    ///   <para>
    ///     Determines if an address is an IPv6 Site Local address
    ///   </para>
    /// </devdoc>
    public bool IsIPv6SiteLocal { get; set; }

    public bool IsIPv6Teredo { get; set; }

    /// <summary>Gets whether the address is an IPv6 Unique Local address.</summary>
    public bool IsIPv6UniqueLocal { get; set; }

    // 0:0:0:0:0:FFFF:x.x.x.x
    public bool IsIPv4MappedToIPv6 { get; set; }
}