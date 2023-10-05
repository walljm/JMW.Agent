namespace JMW.Agent.Common.Models;

public class JmwSystemInfo
{
    public DateTimeOffset? SystemDateTime { get; set; }
    public long? SystemPageSize { get; set; }
    public long? TotalAvailableMemoryBytes { get; set; }
}
