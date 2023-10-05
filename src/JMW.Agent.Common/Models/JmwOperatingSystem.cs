namespace JMW.Agent.Common.Models;

public class JmwOperatingSystem
{
    public PlatformID? Platform { get; set; }
    public string? ServicePack { get; set; }
    public Version? Version { get; set; }
    public string? VersionString { get; set; }
    public string? Architecture { get; set; }
    public string? Description { get; set; }
}
