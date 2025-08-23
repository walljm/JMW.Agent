namespace JMW.Agent.Common.Models;

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
