namespace JMW.Agent.Client;

public sealed class AgentOptions
{
    public string? ServerIp { get; set; }
    public int? ServerPort { get; set; } = 443;
    public string? Token { get; set; }
    public string? ServiceName { get; set; }
}
