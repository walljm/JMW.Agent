namespace JMW.Agent.Common.Models;

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
