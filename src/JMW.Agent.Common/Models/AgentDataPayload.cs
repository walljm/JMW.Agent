using System.ComponentModel.DataAnnotations;

namespace JMW.Agent.Common.Models;

public sealed class AgentDataPayload
{
    [Key]
    public Guid AgentId { get; set; }

    public string ServiceName { get; set; } = string.Empty;
    public string? InfoJson { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
