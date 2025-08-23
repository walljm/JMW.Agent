using JMW.Agent.Common.Models;

namespace JMW.Agent.Server.Models;

public sealed class AgentInformation
{
    public string? ServiceName { get; set; }

    public JmwMachineInformation? MachineInformation { get; set; }
}