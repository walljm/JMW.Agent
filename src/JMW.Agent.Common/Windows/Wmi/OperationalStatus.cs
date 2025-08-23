namespace JMW.Agent.Common.Models;

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
