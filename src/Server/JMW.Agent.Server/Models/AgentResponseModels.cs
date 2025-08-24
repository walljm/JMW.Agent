namespace JMW.Agent.Server.Models;

// For the agent management page (all agents with registration status)
public sealed class AgentRegistrationSummary
{
    public Guid AgentId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public bool IsAuthorized { get; set; }
    public DateTime RegisteredAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? AuthorizedAt { get; set; }
    public string? AuthorizedBy { get; set; }
}

// For the active agents page (authorized agents with machine data availability)
public sealed class ActiveAgentSummary
{
    public Guid AgentId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public DateTime LastDataUpdate { get; set; }
    public DateTime LastSeenAt { get; set; }
    public bool HasMachineData { get; set; }
    public string? MachineName { get; set; }  // Quick preview from machine data
}

// For detailed agent information
public sealed class AgentDetailResponse
{
    public Guid AgentId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? LastDataUpdate { get; set; }
    public JMW.Agent.Common.Models.JmwMachineInformation? MachineInformation { get; set; }
}
