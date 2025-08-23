namespace JMW.Agent.Common.Models;

public sealed class JmwIpv4InterfaceProperties
{
    public bool? UsesWins { get; set; }
    public bool? IsDhcpEnabled { get; set; }
    public bool? IsAutomaticPrivateAddressingActive { get; set; }
    public bool? IsAutomaticPrivateAddressingEnabled { get; set; }
    public int? Index { get; set; }
    public bool? IsForwardingEnabled { get; set; }
    public int? Mtu { get; set; }
}
