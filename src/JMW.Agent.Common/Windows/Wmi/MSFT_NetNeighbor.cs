using System.Net.Sockets;

namespace JMW.Agent.Common.Models;

public class MSFT_NetNeighbor
{
    public string? InstanceID { get; set; }
    public string? Caption { get; set; }
    public string? Description { get; set; }
    public string? ElementName { get; set; }
    public DateTime? InstallDate { get; set; }
    public OperationalStatus[]? OperationalStatus { get; set; }
    public string[]? StatusDescriptions { get; set; }
    public string? Status { get; set; }
    public HealthState HealthState { get; set; } = HealthState.Unknown;
    public CommunicationStatus CommunicationStatus { get; set; }
    public DetailedStatus DetailedStatus { get; set; }
    public OperatingStatus OperatingStatus { get; set; }
    public PrimaryStatus PrimaryStatus { get; set; }
    public EnabledState EnabledState { get; set; } = EnabledState.NotApplicable;
    public string? OtherEnabledState { get; set; }
    public RequestedState RequestedState { get; set; } = RequestedState.NotApplicable;
    public EnabledDefault EnabledDefault { get; set; } = EnabledDefault.Enabled;
    public DateTime? TimeOfLastStateChange { get; set; }
    public RequestedState TransitioningToState { get; set; } = RequestedState.NotApplicable;
    public AvailableRequestedStates[]? AvailableRequestedStates { get; set; }
    public string? Name { get; set; }
    public string? SystemCreationClassName { get; set; }
    public string? SystemName { get; set; }
    public string? CreationClassName { get; set; }
    public string? AccessInfo { get; set; }
    public InfoFormat InfoFormat { get; set; }
    public string? OtherInfoFormatDescription { get; set; }
    public AccessContext AccessContext { get; set; } = AccessContext.Unknown;
    public string? OtherAccessContext { get; set; }
    public string? IPAddress { get; set; }
    public uint InterfaceIndex { get; set; }
    public string? InterfaceAlias { get; set; }
    public string? LinkLayerAddress { get; set; }
    public Store Store { get; set; }
    public State State { get; set; }
    public AddressFamily AddressFamily { get; set; }
}
