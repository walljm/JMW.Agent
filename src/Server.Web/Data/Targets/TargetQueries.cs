using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

public static partial class TargetQueries
{
    // ── Admin: Targets (unifies device + service collection targets) ────────────

    /// <summary>
    /// Lists targets with an optional agent_id filter and keyset pagination.
    /// Pass null for agentId to list across all agents.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(Guid TargetId, Guid AgentId, string Endpoint, string CollectorType, Guid? CredentialId,
            string? Label, bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)> ListTargetsAsync(
            this NpgsqlConnection connection,
            Guid? agentId,
            DateTimeOffset? afterCreatedAt,
            Guid? afterTargetId,
            int limit,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Returns the enabled targets for one agent, for config delivery.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(Guid TargetId, string Endpoint, string CollectorType, Guid? CredentialId, string? Label,
            bool Enabled)>
        ListTargetsForAgentAsync(
            this NpgsqlConnection connection,
            Guid agentId,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Returns all targets (enabled and disabled) for one agent for the Agent Detail page
    /// render. Credential names are resolved in the page model.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(Guid TargetId, string Endpoint, string CollectorType, Guid? CredentialId, string? Label,
            bool Enabled)>
        ListAgentTargetsDetailAsync(
            this NpgsqlConnection connection,
            Guid agentId,
            CancellationToken cancellationToken
        );

    /// <summary>Inserts a target. Returns the inserted target_id.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<TargetIdResult> InsertTargetAsync(
        this NpgsqlConnection connection,
        Guid agentId,
        string endpoint,
        string collectorType,
        Guid? credentialId,
        string? label,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Updates a target's endpoint, collector type, credential, and label.
    /// Returns the target_id, or no rows if not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<TargetIdResult> UpdateTargetAsync(
        this NpgsqlConnection connection,
        Guid targetId,
        string endpoint,
        string collectorType,
        Guid? credentialId,
        string? label,
        CancellationToken cancellationToken
    );

    /// <summary>Deletes a target. Returns the target_id, or no rows if not found.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<TargetIdResult> DeleteTargetAsync(
        this NpgsqlConnection connection,
        Guid targetId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Toggles a target's enabled flag. Returns (target_id, enabled), or no rows if not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid TargetId, bool Enabled)> ToggleTargetAsync(
        this NpgsqlConnection connection,
        Guid targetId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Discovered IPs/services that look collectable via an existing collector type
    /// (ssh/snmp/cert/google-wifi/home-assistant), excluding any (endpoint, collector_type)
    /// pair already configured as a target for this agent.
    /// Endpoint/CollectorType are never actually null at runtime — Postgres reports UNION
    /// ALL result columns as nullable even when every branch is NOT NULL, since it can't
    /// prove non-null across a UNION.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(string? Endpoint, string? CollectorType, string? Hostname, string? Vendor, string? Model)>
        ListTargetCandidatesAsync(
            this NpgsqlConnection connection,
            Guid agentId,
            CancellationToken cancellationToken
        );
}