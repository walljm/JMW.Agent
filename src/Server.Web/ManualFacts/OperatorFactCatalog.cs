using System.Reflection;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Server.ManualFacts;

/// <summary>
/// The write-time authorization surface for unified operator-authored facts
/// (docs/plans/user-provided.md, docs/plans/architecture-operator-facts.md). It answers, for a
/// submitted fact path, whether the path is an <b>override</b> of a catalog constant, an
/// <b>arbitrary</b> operator-invented fact, or one of the carve-outs that may never be authored.
///
/// Three carve-out classes (architecture §4, §5.2, §14, Boss-ratified 2026-07-16):
/// <list type="bullet">
/// <item><b>Identity-bearing fact paths</b> (NFR-8) — <see cref="IdentityBearingFactPaths" />: consts
///   whose projection column <c>DiscoveryMaterializer</c> reads as a device fingerprint or promotion
///   input. Overriding one could corrupt agentless device resolution (wrong merge / spurious
///   duplicate).</item>
/// <item><b>Identity-bearing dimensions</b> (Amendment B) — <see cref="IdentityBearingDimensions" />:
///   a projection dimension whose <i>key</i> is itself a fingerprint (<c>Device[].Lease[]</c>'s key
///   is a MAC address read by <c>GetNewDhcpLocalMacs</c>/<c>GetKnownMacsForIp</c>). No const can
///   express this, so the whole dimension is blocked.</item>
/// <item><b>Derived / metric paths</b> (Amendment A) — <see cref="NonAuthorablePaths" />:
///   <see cref="FactPaths.Derived" /> are recomputed every analysis cycle (a manual value is
///   clobbered) and <see cref="FactPaths.MetricPaths" /> route to <c>metrics_raw</c> as monotonic
///   counters (a hand value is meaningless).</item>
/// </list>
///
/// <see cref="IdentityBearingFactPaths" /> and <see cref="IdentityBearingDimensions" /> are the
/// single documented artifacts NFR-8 requires; their completeness is pinned by the two-arm exact
/// set-equality fitness test against <c>DiscoveryMaterializer.IdentityInputColumns</c>.
/// </summary>
public static class OperatorFactCatalog
{
    /// <summary>
    /// NFR-8 identity-bearing exclusion set — every <see cref="FactPaths" /> const whose projection
    /// column <c>DiscoveryMaterializer</c> reads as a fingerprint or promotion input, plus
    /// <see cref="FactPaths.HwSystemSerial" /> (the agent-direct chassis-serial fingerprint — not a
    /// materializer read, but excluded per Boss's confirmed list). 27 consts + HwSystemSerial.
    /// Kept in exact set-equality with the materializer's declared read set (minus the
    /// <see cref="GapFillOnlyFactPaths" /> exemptions) by the identity fitness test; the mapping is:
    /// <code>
    /// Tier 1 — identity/merge-critical (wrong value ⇒ wrong device merge or spurious duplicate)
    ///   ArpMac                    → proj_device_arp.mac
    ///   DiscoveredMAC             → proj_discovered.mac
    ///   DiscoveredObscuredMAC     → proj_discovered.obscured_mac
    ///   DiscoveredOnvifSerial     → proj_discovered.onvif_serial
    ///   DiscoveredRokuSerial      → proj_discovered.roku_serial
    ///   DiscoveredSnmpSerial      → proj_discovered.snmp_serial
    ///   DiscoveredSsdpUuid        → proj_discovered.ssdp_uuid
    ///   DiscoveredWsdUuid         → proj_discovered.wsd_uuid
    ///   DiscoveredSshHostKey      → proj_discovered.ssh_host_key
    ///   DiscoveredHueBridgeId     → proj_discovered.hue_bridge_id
    ///   DiscoveredOnvifHardwareId → proj_discovered.onvif_hardware_id
    ///   DiscoveredCastId          → proj_discovered.cast_id
    ///   InterfaceMAC              → proj_interfaces.mac_address
    ///   InterfaceObscuredMAC      → proj_interfaces.obscured_mac
    ///   InterfaceIPv4             → proj_interfaces.ipv4 (MAC-reconstruction join key)
    ///   DhcpLocalLeaseIP          → proj_dhcp_local_leases.ip
    /// Tier 2 — promotion inputs (wrong value ⇒ wrong/suppressed promoted metadata)
    ///   DiscoveredHostname        → proj_discovered.hostname
    ///   DiscoveredFriendlyName    → proj_discovered.friendly_name
    ///   DiscoveredVendor          → proj_discovered.vendor
    ///   DiscoveredModel           → proj_discovered.model
    ///   DiscoveredOs              → proj_discovered.os
    ///   DiscoveredDeviceType      → proj_discovered.device_type
    ///   HwSystemVendor            → proj_hardware.system_vendor
    ///   HwSystemModel             → proj_hardware.system_model
    ///   SystemHostname            → proj_systems.hostname
    ///   SystemOsFamily            → proj_systems.os_family
    ///   DhcpLocalLeaseHostname    → proj_dhcp_local_leases.hostname
    /// Plus HwSystemSerial (agent-path fingerprint, documented exception in the fitness test).
    /// Minus SystemFriendlyName (materializer read, but gap-fill-only — see
    /// <see cref="GapFillOnlyFactPaths" />).
    /// </code>
    /// </summary>
    public static readonly IReadOnlySet<string> IdentityBearingFactPaths = new HashSet<string>(StringComparer.Ordinal)
    {
        // Tier 1 — identity/merge-critical
        FactPaths.ArpMac,
        FactPaths.DiscoveredMAC,
        FactPaths.DiscoveredObscuredMAC,
        FactPaths.DiscoveredOnvifSerial,
        FactPaths.DiscoveredRokuSerial,
        FactPaths.DiscoveredSnmpSerial,
        FactPaths.DiscoveredSsdpUuid,
        FactPaths.DiscoveredWsdUuid,
        FactPaths.DiscoveredSshHostKey,
        FactPaths.DiscoveredHueBridgeId,
        FactPaths.DiscoveredOnvifHardwareId,
        FactPaths.DiscoveredCastId,
        FactPaths.InterfaceMAC,
        FactPaths.InterfaceObscuredMAC,
        FactPaths.InterfaceIPv4,
        FactPaths.DhcpLocalLeaseIP,

        // Tier 2 — promotion inputs
        FactPaths.DiscoveredHostname,
        FactPaths.DiscoveredFriendlyName,
        FactPaths.DiscoveredVendor,
        FactPaths.DiscoveredModel,
        FactPaths.DiscoveredOs,
        FactPaths.DiscoveredDeviceType,
        FactPaths.HwSystemVendor,
        FactPaths.HwSystemModel,
        FactPaths.SystemHostname,
        FactPaths.SystemOsFamily,
        FactPaths.DhcpLocalLeaseHostname,

        // Agent-direct chassis-serial fingerprint (not a materializer read — see fitness test).
        FactPaths.HwSystemSerial,
    };

    /// <summary>
    /// Materializer reads that are <b>gap-fill-only</b> and therefore stay operator-authorable
    /// despite appearing in <c>DiscoveryMaterializer.IdentityInputColumns</c>. The materializer
    /// consults these columns solely to decide whether promotion should auto-fill missing display
    /// metadata — the value never participates in fingerprinting or device merging, so an
    /// operator-set value cannot corrupt identity; it merely (correctly) suppresses the auto-fill.
    /// The fitness test subtracts this set from the materializer read set before asserting
    /// equality, making each exemption an explicit, reviewed decision.
    ///
    /// <c>SystemFriendlyName</c>: proj_systems.friendly_name is read by GetPromotionGapRows only
    /// to find devices whose friendly name is still empty. FactPaths.cs documents the path as
    /// directly operator-editable — it is the intended home for an operator-assigned display name.
    /// </summary>
    public static readonly IReadOnlySet<string> GapFillOnlyFactPaths = new HashSet<string>(StringComparer.Ordinal)
    {
        FactPaths.SystemFriendlyName,
    };

    /// <summary>
    /// NFR-8 identity-bearing dimension set (Amendment B). A submission whose <c>DimKey</c> is in
    /// this set is rejected regardless of attribute or catalog/arbitrary classification, because the
    /// dimension's key is a device fingerprint. Today the only device-scoped case is
    /// <c>Device[].Lease[]</c> (<c>proj_dhcp_local_leases</c>), whose <c>Lease</c> key is a MAC read
    /// by <c>GetNewDhcpLocalMacs</c>/<c>GetKnownMacsForIp</c>. Kept in exact set-equality with the
    /// materializer's <c>DimensionKey</c>-tagged reads by the identity fitness test.
    /// </summary>
    public static readonly IReadOnlySet<string> IdentityBearingDimensions = new HashSet<string>(StringComparer.Ordinal)
    {
        "Device|Lease",
    };

    /// <summary>
    /// Every <see cref="FactPaths" /> top-level catalog template (the reflection scope
    /// <c>ManualFactCatalog</c> used). Nested <see cref="FactPaths.Derived" /> consts are
    /// deliberately excluded here (they are not reachable by this reflection and are gated
    /// separately via <see cref="NonAuthorablePaths" />). Used as the near-miss candidate set and
    /// to decide whether a submitted path is a known catalog path (→ override) or not (→ arbitrary).
    /// </summary>
    public static readonly IReadOnlyList<string> AllCatalogPaths = ConstsOf(typeof(FactPaths))
        .OrderBy(p => p, StringComparer.Ordinal)
        .ToArray();

    private static readonly HashSet<string> CatalogSet =
        AllCatalogPaths.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Paths that are catalog-known but never operator-authorable for correctness reasons
    /// (Amendment A): <see cref="FactPaths.Derived" /> (recomputed by analysis) and
    /// <see cref="FactPaths.MetricPaths" /> (monotonic counters in <c>metrics_raw</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> NonAuthorablePaths = BuildNonAuthorable();

    /// <summary>
    /// The overridable catalog offered to the operator (combo box): every catalog template that is
    /// neither identity-bearing (path or dimension) nor non-authorable (Derived/Metric). Ordinal-sorted.
    /// </summary>
    public static readonly IReadOnlyList<string> OverridablePaths = AllCatalogPaths
        .Where(p => !IdentityBearingFactPaths.Contains(p)
                 && !NonAuthorablePaths.Contains(p)
                 && !IdentityBearingDimensions.Contains(Fact.DeriveDimKey(p)))
        .ToArray();

    /// <summary>The classification of a submitted fact-path template.</summary>
    public enum PathClass
    {
        /// <summary>DimKey is an identity-bearing dimension — reject (identity_protected_dimension).</summary>
        IdentityProtectedDimension,

        /// <summary>Template is an identity-bearing const — reject (identity_protected).</summary>
        IdentityProtected,

        /// <summary>Template is a Derived/Metric path — reject (not_authorable).</summary>
        NotAuthorable,

        /// <summary>Template matches a catalog const — this is an override.</summary>
        Override,

        /// <summary>Template matches no catalog const — this is an arbitrary fact (subject to near-miss).</summary>
        Arbitrary,
    }

    /// <summary>
    /// Classifies a submitted template in the exact order the write gate requires (dimension →
    /// identity const → non-authorable → catalog override → arbitrary). The dimension check comes
    /// first so it covers the whole <c>Device[].Lease[]</c> family — catalog value paths,
    /// <c>Expires</c>/<c>Source</c>, and arbitrary attributes alike.
    /// </summary>
    public static PathClass Classify(string template)
    {
        if (IdentityBearingDimensions.Contains(Fact.DeriveDimKey(template)))
        {
            return PathClass.IdentityProtectedDimension;
        }

        if (IdentityBearingFactPaths.Contains(template))
        {
            return PathClass.IdentityProtected;
        }

        if (NonAuthorablePaths.Contains(template))
        {
            return PathClass.NotAuthorable;
        }

        return CatalogSet.Contains(template) ? PathClass.Override : PathClass.Arbitrary;
    }

    /// <summary>
    /// Whether a path is a known catalog template (for display: an override) versus an
    /// operator-invented one (arbitrary). Unlike <see cref="Classify" /> this ignores the
    /// authorability carve-outs — a pre-existing override of a now-protected const still reads as an
    /// override in the table.
    /// </summary>
    public static bool IsOverride(string template) => CatalogSet.Contains(template);

    private static HashSet<string> BuildNonAuthorable()
    {
        HashSet<string> result = ConstsOf(typeof(FactPaths.Derived)).ToHashSet(StringComparer.Ordinal);
        foreach (string metric in FactPaths.MetricPaths)
        {
            result.Add(metric);
        }

        return result;
    }

    private static IEnumerable<string> ConstsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!);
}