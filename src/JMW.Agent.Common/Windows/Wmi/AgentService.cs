using System.ComponentModel.DataAnnotations;

namespace JMW.Agent.Server.Models;

public class AgentService
{
    [Key]
    public string? Name { get; set; }

    public string? InfoJson { get; set; }
}