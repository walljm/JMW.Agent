using System.ComponentModel.DataAnnotations;

namespace JMW.Agent.Client;

public sealed class AgentOptions
{
    [Required(ErrorMessage = "ServerIp is required")]
    public string? ServerIp { get; set; }

    [Range(1, 65535, ErrorMessage = "ServerPort must be between 1 and 65535")]
    public int? ServerPort { get; set; } = 443;

    [Required(ErrorMessage = "ServiceName is required")]
    public string? ServiceName { get; set; }

    public string AgentIdFilePath { get; set; } = "agent.id";
}
