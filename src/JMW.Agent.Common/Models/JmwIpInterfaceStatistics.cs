namespace JMW.Agent.Common.Models;

public sealed class JmwIpInterfaceStatistics
{
    public long? BytesReceived { get; set; }
    public long? BytesSent { get; set; }
    public long? IncomingPacketsDiscarded { get; set; }
    public long? IncomingPacketsWithErrors { get; set; }
    public long? IncomingUnknownProtocolPackets { get; set; }
    public long? NonUnicastPacketsReceived { get; set; }
    public long? NonUnicastPacketsSent { get; set; }
    public long? OutgoingPacketsDiscarded { get; set; }
    public long? OutgoingPacketsWithErrors { get; set; }
    public long? OutputQueueLength { get; set; }
    public long? UnicastPacketsReceived { get; set; }
    public long? UnicastPacketsSent { get; set; }
}
