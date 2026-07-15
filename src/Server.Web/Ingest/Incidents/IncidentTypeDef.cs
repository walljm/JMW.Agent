using JMW.Discovery.Core;

namespace JMW.Discovery.Server.Incidents;

/// <summary>
/// Declares one value-driven incident type: which fact path to watch, when a value opens or
/// resolves it, and how long a resolved incident stays reopenable (flap suppression). Mirrors
/// <see cref="Projections.ProjectionColumnDef" />'s "one FactPaths constant, routed by (DimKey,
/// Attribute)" shape — see <see cref="IncidentEvaluator" />.
/// ShouldOpen/ShouldResolve are independent, not each other's negation, so a value can fall in a
/// dead zone that does neither (e.g. filesystem_full's 85–90% hysteresis band).
/// </summary>
public sealed record IncidentTypeDef(
    string IncidentType,
    string EntityKind, // 'device' | 'service' | 'agent'
    string Attribute, // FactPaths constant, full path (e.g. "Device[].Disk[].Smart.OverallHealth")
    Func<FactValue, bool> ShouldOpen,
    Func<FactValue, bool> ShouldResolve,
    Func<FactValue, string?> Detail,
    TimeSpan ReopenWindow
);