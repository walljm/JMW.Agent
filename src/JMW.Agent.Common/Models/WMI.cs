using System.Management;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace JMW.Agent.Common.Models;

internal class WMI
{
    public static MSFT_NetNeighbor[] GetNetNeighbors()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Array.Empty<MSFT_NetNeighbor>();
        }

        var scope = new ManagementScope($@"\\localhost\root\StandardCimv2");

        return QueryCim<MSFT_NetNeighbor>(scope, "MSFT_NetNeighbor").ToArray();
    }

    private static IEnumerable<T> QueryCim<T>(ManagementScope scope, string cls)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield break;
        }

        var s = new ManagementObjectSearcher(scope, new WqlObjectQuery($"SELECT * FROM {cls}"));

        foreach (var o in s.Get().Cast<ManagementObject>())
        {
            yield return o.ToType<T>();
        }
    }
}

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

public enum Store
{
    Persistent = 0,
    Active = 1,
}

public enum State
{
    Unreachable = 0,
    Incomplete = 1,
    Probe = 2,
    Delay = 3,
    Stale = 4,
    Reachable = 5,
    Permanent = 6,
}

public enum RequestedState
{
    Unknown = 0,
    Enabled = 2,
    Disabled = 3,
    ShutDown = 4,
    NoChange = 5,
    Offline = 6,
    Test = 7,
    Deferred = 8,
    Quiesce = 9,
    Reboot = 10,
    Reset = 11,
    NotApplicable = 12,
}

public enum PrimaryStatus
{
    Unknown = 0,
    OK = 1,
    Degraded = 2,
    Error = 3,
}

public enum OperationalStatus
{
    Unknown = 0,
    Other = 1,
    OK = 2,
    Degraded = 3,
    Stressed = 4,
    PredictiveFailure = 5,
    Error = 6,
    NonRecoverableError = 7,
    Starting = 8,
    Stopping = 9,
    Stopped = 10,
    InService = 11,
    NoContact = 12,
    LostCommunication = 13,
    Aborted = 14,
    Dormant = 15,
    SupportingEntityInError = 16,
    Completed = 17,
    PowerMode = 18,
}

public enum OperatingStatus
{
    Unknown = 0,
    NotAvailable = 1,
    Servicing = 2,
    Starting = 3,
    Stopping = 4,
    Stopped = 5,
    Aborted = 6,
    Dormant = 7,
    Completed = 8,
    Migrating = 9,
    Emigrating = 10,
    Immigrating = 11,
    Snapshotting = 12,
    ShuttingDown = 13,
    InTest = 14,
    Transitioning = 15,
    InService = 16,
}

public enum InfoFormat
{
    Other = 1,
    HostName = 2,
    IPv4Address = 3,
    IPv6Address = 4,
    IPXAddress = 5,
    DECnetAddress = 6,
    SNAAddress = 7,
    AutonomousSystemNumber = 8,
    MPLSLabel = 9,
    IPv4SubnetAddress = 10,
    IPv6SubnetAddress = 11,
    IPv4AddressRange = 12,
    IPv6AddressRange = 13,
    DialString = 100,
    EthernetAddress = 101,
    TokenRingAddress = 102,
    ATMAddress = 103,
    FrameRelayAddress = 104,
    URL = 200,
    FQDN = 201,
    UserFQDN = 202,
    DER_ASN1_DN = 203,
    DER_ASN1_GN = 204,
    KeyID = 205,
    ParameterizedURL = 206,
}

public enum HealthState
{
    Unknown = 0,
    OK = 5,
    DegradedOrWarning = 10,
    MinorFailure = 15,
    MajorFailure = 20,
    CriticalFailure = 25,
    NonRecoverableError = 30,
}

public enum EnabledState
{
    Unknown = 0,
    Other = 1,
    Enabled = 2,
    Disabled = 3,
    ShuttingDown = 4,
    NotApplicable = 5,
    EnabledButOffline = 6,
    InTest = 7,
    Deferred = 8,
    Quiesce = 9,
    Starting = 10,
}

public enum EnabledDefault
{
    Enabled = 2,
    Disabled = 3,
    NotApplicable = 5,
    EnabledButOffline = 6,
    NoDefault = 7,
    Quiesce = 9,
}

public enum DetailedStatus
{
    NotAvailable = 0,
    NoAdditionalInformation = 1,
    Stressed = 2,
    PredictiveFailure = 3,
    NonRecoverableError = 4,
    SupportingEntityInError = 5,
}

public enum CommunicationStatus
{
    Unknown = 0,
    NotAvailable = 1,
    CommunicationOK = 2,
    LostCommunication = 3,
    NoContact = 4,
}

public enum AvailableRequestedStates
{
    Enabled = 2,
    Disabled = 3,
    ShutDown = 4,
    Offline = 6,
    Test = 7,
    Defer = 8,
    Quiesce = 9,
    Reboot = 10,
    Reset = 11,
}

public enum AccessContext
{
    Unknown = 0,
    Other = 1,
    DefaultGateway = 2,
    DNSServer = 3,
    SNMPTrapDestination = 4,
    MPLSTunnelDestination = 5,
    DHCPServer = 6,
    SMTPServer = 7,
    LDAPServer = 8,
    NTPServer = 9,
    ManagementService = 10,
}

public static class Extensions
{
    public static T ToType<T>(this ManagementObject obj)
    {
        var o = Activator.CreateInstance<T>();
        var type = typeof(T);
        foreach (var prop in obj.Properties)
        {
            var p = type.GetProperty(prop.Name);
            if (p == null)
            {
                Console.WriteLine($"{prop.Name} with {prop.Type} isn't on the type");
                continue;
            }
            if (prop.Type == CimType.None || prop.Value == null)
            {
                continue;
            }

            if (prop.IsArray)
            {
                var src = (Array)prop.Value;
                var arr = (Array)Activator.CreateInstance(p.PropertyType, src.Length);
                var et = p.PropertyType.GetElementType();

                for (var i = 0; i < src.Length; i++)
                {
                    var v = et.IsEnum ? Enum.Parse(et, src.GetValue(i).ToString(), true)
                        : Convert.ChangeType(src.GetValue(i), et);
                    arr.SetValue(v, i);
                }
                p.SetValue(o, arr);
            }
            else
            {
                if (p.PropertyType.IsEnum)
                {
                    p.SetValue(o, Enum.Parse(p.PropertyType, prop.Value.ToString(), true));
                }
                else if (p.PropertyType == typeof(DateTime?) || p.PropertyType == typeof(DateTime))
                {
                    p.SetValue(o, ManagementDateTimeConverter.ToDateTime(prop.Value.ToString()));
                }
                else
                {
                    p.SetValue(o, prop.Value);
                }
            }
        }

        return o;
    }
}
