namespace JMW.Agent.Common.Models;

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
