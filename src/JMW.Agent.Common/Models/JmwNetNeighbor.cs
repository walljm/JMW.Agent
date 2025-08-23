using System.Net.NetworkInformation;

namespace JMW.Agent.Common.Models;

public sealed class JmwNetNeighbor
{
    public string? Name { get; set; }
    public Store? Store { get; set; }
    public State? State { get; set; }

    public uint? InterfaceIndex { get; set; }
    public string? InterfaceAlias { get; set; }

    public JmwIpAddress? IPAddress { get; set; }
    public PhysicalAddress? LinkLayerAddress { get; set; }
}
