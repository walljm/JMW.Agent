using JMW.Discovery.Core;

using Npgsql;

namespace JMW.Discovery.Server.Projections;

/// <summary>
/// A projection maintains a queryable table derived from a subset of facts.
/// Each implementation owns its own SQL and knows which facts it cares about.
/// </summary>
public interface IProjection
{
    /// <summary>
    /// The segment names that form the row key, in order.
    /// e.g. ["Device", "Interface"] for a per-interface projection.
    /// </summary>
    IReadOnlyList<string> DimensionNames { get; }

    /// <summary>
    /// The attribute names this projection handles.
    /// Only facts with a matching attribute will be routed here.
    /// </summary>
    IReadOnlySet<string> TrackedAttributes { get; }

    /// <summary>
    /// Applies a batch of pre-routed facts to the projection table.
    /// Implementations must avoid writes when values have not changed.
    /// </summary>
    Task ApplyAsync(IReadOnlyList<RoutedFact> facts, NpgsqlConnection conn, CancellationToken ct);
}

/// <summary>
/// A fact that has already been parsed and matched to a projection.
/// </summary>
public sealed record RoutedFact(
    string[] DimensionKeys, // e.g. ["router-1", "eth0"]
    string Attribute, // e.g. "Speed"
    FactValue Value,
    DateTimeOffset CollectedAt,
    Guid? AgentId = null
);