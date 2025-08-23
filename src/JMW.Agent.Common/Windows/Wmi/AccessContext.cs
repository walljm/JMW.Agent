namespace JMW.Agent.Common.Models;

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
