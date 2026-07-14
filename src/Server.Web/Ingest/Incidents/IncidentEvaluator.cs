using JMW.Discovery.Core;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Incidents;

/// <summary>
/// Evaluates incoming facts against the value-driven incident type registry, alongside
/// FactIngestPipeline's existing AppendAsync/RouteAsync tasks. Routing is O(1) per fact —
/// indexed by (DimKey, Attribute), same shape as ProjectionRouter — so this costs one dictionary
/// probe for every fact that ISN'T an incident signal (the overwhelming majority).
/// </summary>
public sealed class IncidentEvaluator
{
    private readonly NpgsqlDataSource _db;
    private readonly Dictionary<(string DimKey, string Attribute), List<IncidentTypeDef>> _index;

    public IncidentEvaluator(NpgsqlDataSource db, IEnumerable<IncidentTypeDef> incidentTypes)
    {
        _db = db;
        _index = BuildIndex(incidentTypes);
    }

    public async Task EvaluateAsync(IEnumerable<Fact> facts, CancellationToken ct = default)
    {
        List<(IncidentTypeDef Def, string EntityId, FactValue Value)> matches = [];

        foreach (Fact fact in facts)
        {
            if (fact.DimKey.Length == 0 || fact.Attribute.Length == 0)
            {
                continue;
            }

            if (!_index.TryGetValue((fact.DimKey, fact.Attribute), out List<IncidentTypeDef>? defs))
            {
                continue;
            }

            // The entity this incident is scoped to is always the FIRST list-dimension key
            // (the device/service/agent id) — a sub-dimension (e.g. which disk, which container)
            // is carried in the incident's detail text, not a second identity component. Two
            // simultaneously-failing sub-entities on the same device collapse into one incident
            // row; see IncidentTypeDef's remarks for why that trade-off is acceptable here.
            string? entityId = null;
            foreach (FactSegment seg in fact.ParseId())
            {
                if (seg.IsList)
                {
                    entityId = seg.Key;
                    break;
                }
            }

            if (entityId is null)
            {
                continue;
            }

            foreach (IncidentTypeDef def in defs)
            {
                matches.Add((def, entityId, fact.Value));
            }
        }

        if (matches.Count == 0)
        {
            return;
        }

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        foreach ((IncidentTypeDef def, string entityId, FactValue value) in matches)
        {
            if (def.ShouldOpen(value))
            {
                await conn.OpenOrTouchIncidentAsync(
                        def.EntityKind,
                        entityId,
                        def.IncidentType,
                        def.Detail(value),
                        def.ReopenWindow.TotalSeconds,
                        ct
                    )
                    .ExecuteAsync(ct);
            }
            else if (def.ShouldResolve(value))
            {
                await conn.ResolveIncidentAsync(def.EntityKind, entityId, def.IncidentType, ct).ExecuteAsync(ct);
            }

            // Neither open nor resolve (e.g. filesystem_full's 85-90% dead zone): no write —
            // an already-open incident simply keeps its last opening detail until the value
            // crosses one boundary or the other.
        }
    }

    private static Dictionary<(string, string), List<IncidentTypeDef>> BuildIndex(
        IEnumerable<IncidentTypeDef> incidentTypes
    )
    {
        Dictionary<(string, string), List<IncidentTypeDef>> index = new();
        foreach (IncidentTypeDef def in incidentTypes)
        {
            (string, string) key = (Fact.DeriveDimKey(def.Attribute), Fact.DeriveAttribute(def.Attribute));
            if (!index.TryGetValue(key, out List<IncidentTypeDef>? list))
            {
                list = [];
                index[key] = list;
            }

            list.Add(def);
        }

        return index;
    }
}
