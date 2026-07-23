using Npgsql;

namespace JMW.Discovery.Server.Ingest.Context;

/// <summary>
/// A context derivation mirrors <see cref="JMW.Discovery.Core.Analysis.IDerivation" />'s
/// contract — declared inputs, a deterministic "decide between them" — but over inputs the
/// per-batch analysis engine structurally cannot see: cross-entity rows (an observer's
/// proj_discovered/proj_device_arp entries matched to the subject by MAC), non-fact state
/// (the device_fingerprints registry, raw-SQL-written columns), and values whose recompute
/// must be triggered by ANOTHER device's batch. See docs/plans/context-derivations.md.
///
/// Implementations are stateless: one set-based query resolves the value for every device in
/// one pass (recompute-all deliberately replaces affected-subject bookkeeping — it is
/// self-healing by construction and cheaper at this system's scale). The engine owns
/// triggering, debounce, change suppression, and emission.
/// </summary>
public interface IContextDerivation
{
    /// <summary>Stable name for logs and <see cref="JMW.Discovery.Core.Fact.SourceName" />.</summary>
    string Name { get; }

    /// <summary>
    /// Tables whose writes make this derivation's output potentially stale — gates the pass on
    /// the batch's touched-tables set, exactly like <c>DiscoveryMaterializer.RelevantTables</c>.
    /// Generous sets are cheap (recompute-all + debounce); only projection tables appear in
    /// touched-tables, so registry-only inputs (device_fingerprints) are covered by listing the
    /// projections whose ingest coincides with fingerprint writes.
    /// </summary>
    IReadOnlySet<string> TriggerTables { get; }

    /// <summary>The Derived.Identity* fact path template the resolved value is emitted under.</summary>
    string OutputPath { get; }

    /// <summary>Minimum time between passes — ARP-ish trigger tables are touched by nearly
    /// every batch, so this bounds recompute frequency.</summary>
    TimeSpan MinInterval { get; }

    /// <summary>Resolves the current best value for every device: one set-based query.</summary>
    IAsyncEnumerable<Queries.ResolvedContextRow> ResolveAsync(NpgsqlConnection connection, CancellationToken ct);
}