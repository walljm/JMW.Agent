using System.Reflection;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Server.ManualFacts;

/// <summary>
/// The set of existing <see cref="FactPaths" /> constants an operator is allowed to set directly
/// (see docs/plans/user-provided.md, "Setting an existing field"). Reflects the same way
/// <c>FactPathRoutingFitnessTests</c> does, then narrows to paths that are safe to hand-author:
/// - <see cref="FactPaths.Derived" /> constants are excluded — those are computed by
///   <c>AnalysisEngine</c> from other facts, not meant to be authored directly; a manual value
///   would just be recomputed over on the next cycle that has real inputs.
/// - <see cref="ServicePaths" /> constants are excluded — this feature is device-scoped only.
/// - <see cref="FactPaths.MetricPaths" /> are excluded — those route to metrics_raw, not
///   facts_history, and are monotonic counters by construction; a manual value makes no sense.
/// - Only paths with exactly one list dimension (the <c>Device[]</c> root) qualify. A path with a
///   second list dimension (e.g. <c>Device[].Interface[].SpeedBps</c>) needs a key an operator
///   editing "this device" doesn't have (which interface?), so it's out of scope here.
/// Every entry is always written as <see cref="FactValueKind.String" /> — <see cref="FactPaths" />
/// constants carry no declared value-kind today, so a path whose established value is actually
/// numeric/bool/date-typed may fail to update a typed projection column it also feeds (the
/// fact itself still lands in facts_history and renders on the device page either way). See
/// docs/plans/user-provided.md's "Scoping call" note. Building a proper per-path type registry is
/// a reasonable follow-up, not v1 scope.
/// </summary>
public static class ManualFactCatalog
{
    public static readonly IReadOnlyList<string> EditablePaths = BuildCatalog();

    private static List<string> BuildCatalog()
    {
        List<string> result = [];
        foreach (FieldInfo field in typeof(FactPaths).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field is not { IsLiteral: true, IsInitOnly: false } || field.FieldType != typeof(string))
            {
                continue;
            }

            string path = (string)field.GetValue(null)!;
            if (FactPaths.MetricPaths.Contains(path))
            {
                continue;
            }

            FactSegment[] segments = FactSegment.ParsePath(path);
            if (segments.Count(s => s.IsList) != 1 || !segments[0].IsList
             || !string.Equals(segments[0].Name, "Device", StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(path);
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }
}