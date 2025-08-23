namespace JMW.Agent.Common.Models;

public sealed class JmwProcessor
{
    public int? ProcessorCount { get; set; }
    public string? ProcessArchitecture { get; set; }
    public bool? Is64BitOperatingSystem { get; set; }
    public string? ProcessorIdentity { get; set; }
    public string? ProcessorLevel { get; set; }
    public string? ProcessorRevision { get; set; }
}
