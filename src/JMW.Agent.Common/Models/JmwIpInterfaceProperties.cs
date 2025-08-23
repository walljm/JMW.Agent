namespace JMW.Agent.Common.Models;

public sealed class JmwIpInterfaceProperties
{
    public bool? IsDnsEnabled { get; set; }
    public string? DnsSuffix { get; set; }
    public bool? IsDynamicDnsEnabled { get; set; }
    public JmwUnicastIpAddressInformation[]? UnicastAddresses { get; set; }
    public JmwMulticastIpAddressInformation[]? MulticastAddresses { get; set; }
    public JmwIpAddressInformation[]? AnycastAddresses { get; set; }
    public JmwIpAddress[]? DnsAddresses { get; set; }
    public JmwIpAddress[]? GatewayAddresses { get; set; }
    public JmwIpAddress[]? DhcpServerAddresses { get; set; }
    public JmwIpAddress[]? WinsServersAddresses { get; set; }
    public JmwIpv4InterfaceProperties? IPv4Properties { get; set; }
    public JmwIpv6InterfaceProperties? IPv6Properties { get; set; }
}
