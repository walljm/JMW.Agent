namespace JMW.Agent.Common.Models;

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
