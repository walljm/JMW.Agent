using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Server.Projections;

/// <summary>
/// The identity-signal fact paths projected into <c>materialization_facts</c>
/// (docs/plans/architecture-identity-facts.md §4) instead of a <c>proj_discovered</c> column.
/// Adding a new scanner fingerprint = add its <see cref="FactPaths" /> const here — no migration,
/// no <see cref="ProjectionLibrary" /> edit. Every path here is Device[].Discovered[].*-scoped and
/// text-valued; enforced by construction since these are the only paths routed to
/// <see cref="IdentityFactProjection" />, which is Device|Discovered-dimensioned.
/// </summary>
public static class IdentitySignalPaths
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        FactPaths.DiscoveredOnvifSerial,
        FactPaths.DiscoveredRokuSerial,
        FactPaths.DiscoveredSnmpSerial,
        FactPaths.DiscoveredSsdpUuid,
        FactPaths.DiscoveredWsdUuid,
        FactPaths.DiscoveredHueBridgeId,
        FactPaths.DiscoveredOnvifHardwareId,
        FactPaths.DiscoveredCastId,
        FactPaths.DiscoveredDeviceType,
        FactPaths.DiscoveredOs,
        FactPaths.DiscoveredSshHostKey,
    };
}
