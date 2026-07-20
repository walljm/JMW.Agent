namespace JMW.Discovery.Server.Queries;

// Single-column result types shared by the per-domain query classes under Data/.
// Named records are required because C# does not support 1-element tuple syntax.
//
// The query methods are grouped by domain, one static class + its .sql files per
// directory under Data/. The [DatabaseCommand] source generator resolves each
// method's .sql file relative to the declaring file's directory, and names its
// generated output per class — so each domain must be its own uniquely-named
// class (AuthQueries, AgentQueries, …), not partials of a single class:
//   Data/Auth         — AuthQueries        (sessions, users, bootstrap)
//   Data/Agents       — AgentQueries       (registration, heartbeat, admin, config, cycles)
//   Data/Credentials  — CredentialQueries  (credential CRUD)
//   Data/Targets      — TargetQueries      (unified device + service collection targets)
//   Data/Discovery    — DiscoveryQueries   (materializer: new-MAC/serial lookups + upserts)
//   Data/Devices      — DeviceQueries      (device reporting + detail tabs)
//   Data/Reporting    — ReportingQueries   (fleet-wide lists + dashboard)
//   Data/Services     — ServiceQueries     (service reporting)
//   Data/Maintenance  — MaintenanceQueries (OUI, retention, audit)
//
// Count is nullable because COUNT(*) is reported as nullable in SchemaOnly mode.
public readonly record struct AdminCountResult(long? Count);

public readonly record struct UsernameResult(string Username);

public readonly record struct SessionIdResult(string SessionId);

public readonly record struct UserIdResult(Guid UserId);

public readonly record struct AuditIdResult(long Id);

public readonly record struct IncidentIdResult(long Id);

public readonly record struct ChangeEventIdResult(long Id);

public readonly record struct EntityIdResult(string EntityId);

public readonly record struct AgentIdResult(Guid AgentId);

public readonly record struct HeartbeatStatusResult(string Status, string? Os, string? Arch);

public readonly record struct CredentialIdResult(Guid CredentialId);

public readonly record struct TargetIdResult(Guid TargetId);

// InUse is nullable because Postgres reports a boolean EXPRESSION result (EXISTS)
// as nullable in SchemaOnly mode, even though EXISTS never returns NULL.
public readonly record struct InUseResult(bool? InUse);

public readonly record struct DiscoveredMacResult(string? Mac);

public readonly record struct DeviceLivenessSettingsResult(int WindowHours);

// Nullable: the IP comes through a UNION ALL, reported nullable in SchemaOnly mode.
public readonly record struct ResolvedIpResult(string? Ip);

// Nullable: the ->> extraction is a computed expression, reported nullable in SchemaOnly mode.
public readonly record struct CollectionKeyResult(string? CollectionKey);

public readonly record struct MetadataIdResult(Guid Id);

public readonly record struct CertsExpiringResult(long? CertsExpiring);

/// <summary>
/// A row from the "new discovered / DHCP" materializer queries — the shared identity +
/// promotion fields, mapped positionally from a common SELECT column order (mac, ip,
/// hostname, onvif_serial, roku_serial, snmp_serial, ssdp_uuid, wsd_uuid, vendor, model, os).
/// One record type for all four queries retires the repeated 11-field positional tuple.
/// </summary>
public readonly record struct DiscoveredRow(
    string? Mac,
    string? Ip,
    string? Hostname,
    string? OnvifSerial,
    string? RokuSerial,
    string? SnmpSerial,
    string? SsdpUuid,
    string? WsdUuid,
    string? Vendor,
    string? Model,
    string? Os
)
{
    /// <summary>
    /// Normalizes blank/whitespace-only fields to null — values arrive raw from the
    /// projection columns, and downstream fingerprinting/promotion expects null, not "".
    /// </summary>
    public DiscoveredRow Clean() => new(
        N(Mac),
        N(Ip),
        N(Hostname),
        N(OnvifSerial),
        N(RokuSerial),
        N(SnmpSerial),
        N(SsdpUuid),
        N(WsdUuid),
        N(Vendor),
        N(Model),
        N(Os)
    );

    private static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

// proj_discovered_services.service is NOT NULL, so the value is always present.
public readonly record struct ServiceResult(string Service);

public readonly record struct TableExistsResult(bool? Exists);

public readonly record struct AgentStatusResult(string? Status);

public readonly record struct DeviceRefResult(string Device);

public readonly record struct CycleIdResult(long CycleId);

// TableName is nullable because FirstOrDefaultAsync yields default(struct) — a null
// TableName — when the UPDATE ... RETURNING matched no row (the not-found signal).
public readonly record struct TableNameResult(string? TableName);

// facts_history.id is TEXT (the full human-readable fact path), not a Guid.
public readonly record struct FactIdResult(string Id);