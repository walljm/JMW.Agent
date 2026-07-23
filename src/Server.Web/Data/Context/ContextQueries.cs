using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

/// <summary>
/// Set-based resolve queries for the context derivations (docs/plans/context-derivations.md §4):
/// each computes the current "best value" of one identity signal for EVERY device in one indexed
/// query — the set form of the per-row laterals DeviceListApi's BaseCte used to run per request.
/// Consumed by <c>ContextDerivationEngine</c>, which diffs the results against its
/// change-suppression cache and emits only changed values as Derived.Identity* facts.
/// </summary>
public static partial class ContextQueries
{
    /// <summary>Current real OS hostname per device, from proj_systems (covers all three
    /// hostname write paths, including the raw non-fact UpsertDeviceSystem one). Own row type:
    /// device here is the NOT NULL base column, not a computed cast like the other three.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<ResolvedHostnameRow> ResolveIdentityHostnameAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>Newest real MAC fingerprint per device — registry-only state
    /// (device_fingerprints), never present in facts_history.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<ResolvedContextRow> ResolveIdentityMacAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>Display-name rollup per device: operator/promoted friendly name, else the best
    /// observer-recorded name matched by newest MAC, else the real hostname.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<ResolvedContextRow> ResolveIdentityFriendlyNameAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>Best identity IP per device: four candidate sources ranked by identity quality
    /// (ip_identity_rank), IPv4-before-IPv6, then recency.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<ResolvedContextRow> ResolveIdentityIpAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );
}

/// <summary>One resolved (device, value) pair. Both nullable because the columns are computed
/// (casts/COALESCE) in most of the resolve queries, so Postgres reports them nullable regardless
/// of the WHERE guards; the engine skips blank rows.</summary>
public readonly record struct ResolvedContextRow(string? Device, string? Value);

/// <summary>ResolveIdentityHostname's row: device is proj_systems' NOT NULL key column read
/// directly (the schema validator's nullability check is symmetric-strict, so it can't share
/// <see cref="ResolvedContextRow" />'s nullable Device).</summary>
public readonly record struct ResolvedHostnameRow(string Device, string? Value);