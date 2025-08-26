using System.ComponentModel.DataAnnotations;

namespace JMW.Agent.Server.Models;

public sealed class RegisteredAgent
{
    [Key]
    public Guid AgentId { get; set; }

    [Required]
    [MaxLength(255)]
    public string ServiceName { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string OperatingSystem { get; set; } = string.Empty;

    public bool IsAuthorized { get; set; } = false;

    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public DateTime? AuthorizedAt { get; set; }

    [MaxLength(100)]
    public string? AuthorizedBy { get; set; }
}
