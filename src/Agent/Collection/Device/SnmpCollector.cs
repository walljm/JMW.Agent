using System.Net;

using JMW.Discovery.Agent.Collection.Device.EnterpriseNumbers;
using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Device;

public sealed class SnmpCollector : IDeviceCollector
{
    private static readonly ILogger<SnmpCollector> Log = AgentLog.CreateLogger<SnmpCollector>();

    // ── sysGroup ──────────────────────────────────────────────────────────────
    private static readonly ObjectIdentifier SysDescr = new("1.3.6.1.2.1.1.1.0");
    private static readonly ObjectIdentifier SysObjectID = new("1.3.6.1.2.1.1.2.0");
    private static readonly ObjectIdentifier SysUpTime = new("1.3.6.1.2.1.1.3.0");
    private static readonly ObjectIdentifier SysContact = new("1.3.6.1.2.1.1.4.0");
    private static readonly ObjectIdentifier SysName = new("1.3.6.1.2.1.1.5.0");
    private static readonly ObjectIdentifier SysLocation = new("1.3.6.1.2.1.1.6.0");

    // SNMP-FRAMEWORK-MIB: stable engine ID (RFC 3411). Available on most managed devices.
    private static readonly ObjectIdentifier SnmpEngineIdOid = new("1.3.6.1.6.3.10.2.1.1.0");

    // ── ifTable (MIB-II 2.2.1) ───────────────────────────────────────────────
    private static readonly ObjectIdentifier IfDescr = new("1.3.6.1.2.1.2.2.1.2");
    private static readonly ObjectIdentifier IfType = new("1.3.6.1.2.1.2.2.1.3");
    private static readonly ObjectIdentifier IfMtu = new("1.3.6.1.2.1.2.2.1.4");
    private static readonly ObjectIdentifier IfSpeed = new("1.3.6.1.2.1.2.2.1.5");
    private static readonly ObjectIdentifier IfPhysAddress = new("1.3.6.1.2.1.2.2.1.6");
    private static readonly ObjectIdentifier IfAdminStatus = new("1.3.6.1.2.1.2.2.1.7");
    private static readonly ObjectIdentifier IfOperStatus = new("1.3.6.1.2.1.2.2.1.8");
    private static readonly ObjectIdentifier IfInOctets = new("1.3.6.1.2.1.2.2.1.10");
    private static readonly ObjectIdentifier IfOutOctets = new("1.3.6.1.2.1.2.2.1.16");

    // ── ifXTable (IF-MIB) ─────────────────────────────────────────────────────
    private static readonly ObjectIdentifier IfName = new("1.3.6.1.2.1.31.1.1.1.1");
    private static readonly ObjectIdentifier IfHighSpeed = new("1.3.6.1.2.1.31.1.1.1.15");
    private static readonly ObjectIdentifier IfAlias = new("1.3.6.1.2.1.31.1.1.1.18");
    private static readonly ObjectIdentifier IfHCInOctets = new("1.3.6.1.2.1.31.1.1.1.6");
    private static readonly ObjectIdentifier IfHCOutOctets = new("1.3.6.1.2.1.31.1.1.1.10");

    // ── ipAddrTable ───────────────────────────────────────────────────────────
    // Keyed by IP address; column 2 = ifIndex binding to the interface.
    private static readonly ObjectIdentifier IpAdEntIfIndex = new("1.3.6.1.2.1.4.20.1.2");

    // ── ipNetToMediaTable (ARP) ───────────────────────────────────────────────
    // Column 2 = PhysAddress (MAC), column 4 = Type (1=other,2=invalid,3=dynamic,4=static)
    private static readonly ObjectIdentifier IpNetToMediaPhysAddr = new("1.3.6.1.2.1.4.22.1.2");
    private static readonly ObjectIdentifier IpNetToMediaType = new("1.3.6.1.2.1.4.22.1.4");

    // ── ENTITY-MIB (entPhysicalTable) ────────────────────────────────────────
    private static readonly ObjectIdentifier EntPhysicalDescr = new("1.3.6.1.2.1.47.1.1.1.1.2");
    private static readonly ObjectIdentifier EntPhysicalClass = new("1.3.6.1.2.1.47.1.1.1.1.5");
    private static readonly ObjectIdentifier EntPhysicalName = new("1.3.6.1.2.1.47.1.1.1.1.7");
    private static readonly ObjectIdentifier EntPhysicalHwRev = new("1.3.6.1.2.1.47.1.1.1.1.8");
    private static readonly ObjectIdentifier EntPhysicalFwRev = new("1.3.6.1.2.1.47.1.1.1.1.9");
    private static readonly ObjectIdentifier EntPhysicalSwRev = new("1.3.6.1.2.1.47.1.1.1.1.10");
    private static readonly ObjectIdentifier EntPhysicalSerialNum = new("1.3.6.1.2.1.47.1.1.1.1.11");
    private static readonly ObjectIdentifier EntPhysicalMfgName = new("1.3.6.1.2.1.47.1.1.1.1.12");
    private static readonly ObjectIdentifier EntPhysicalModelName = new("1.3.6.1.2.1.47.1.1.1.1.13");
    private static readonly ObjectIdentifier EntPhysicalIsFRU = new("1.3.6.1.2.1.47.1.1.1.1.16");

    // ── LLDP-MIB ─────────────────────────────────────────────────────────────
    private static readonly ObjectIdentifier LldpRemChassisIdSubtype = new("1.0.8802.1.1.2.1.4.1.1.4");
    private static readonly ObjectIdentifier LldpRemChassisId = new("1.0.8802.1.1.2.1.4.1.1.5");
    private static readonly ObjectIdentifier LldpRemPortId = new("1.0.8802.1.1.2.1.4.1.1.7");
    private static readonly ObjectIdentifier LldpRemSysName = new("1.0.8802.1.1.2.1.4.1.1.9");
    private static readonly ObjectIdentifier LldpRemManAddrIfId = new("1.0.8802.1.1.2.1.4.2.1.4");

    // lldpLocPortTable — resolves lldpLocPortNum (the local-port index shared with
    // lldpRemTable's first sub-index) to a human-readable local port. Per RFC, when a
    // port has an ifIndex, lldpLocPortDesc carries the same value as ifDescr — so reading
    // it directly avoids a separate lldpLocPortNum→ifIndex mapping step.
    private static readonly ObjectIdentifier LldpLocPortIdSubtype = new("1.0.8802.1.1.2.1.3.7.1.2");
    private static readonly ObjectIdentifier LldpLocPortId = new("1.0.8802.1.1.2.1.3.7.1.3");
    private static readonly ObjectIdentifier LldpLocPortDesc = new("1.0.8802.1.1.2.1.3.7.1.4");

    // lldpLocPortIdSubtype 5 = interfaceName; lldpLocPortId is then the ifName string itself.
    private const int LldpPortIdSubtypeInterfaceName = 5;

    // ── BRIDGE-MIB (dot1dBase / dot1dStp) ────────────────────────────────────
    private static readonly ObjectIdentifier Dot1dBaseBridgeAddress = new("1.3.6.1.2.1.17.1.1.0");
    private static readonly ObjectIdentifier Dot1dBasePortIfIndex = new("1.3.6.1.2.1.17.1.4.1.2");
    private static readonly ObjectIdentifier Dot1dStpProtocolSpecification = new("1.3.6.1.2.1.17.2.1.0");
    private static readonly ObjectIdentifier Dot1dStpPriority = new("1.3.6.1.2.1.17.2.2.0");
    private static readonly ObjectIdentifier Dot1dStpDesignatedRoot = new("1.3.6.1.2.1.17.2.5.0");
    private static readonly ObjectIdentifier Dot1dStpRootCost = new("1.3.6.1.2.1.17.2.6.0");
    private static readonly ObjectIdentifier Dot1dStpRootPort = new("1.3.6.1.2.1.17.2.7.0");
    private static readonly ObjectIdentifier Dot1dStpPortState = new("1.3.6.1.2.1.17.2.15.1.3");
    private static readonly ObjectIdentifier Dot1dStpPortPathCost = new("1.3.6.1.2.1.17.2.15.1.5");
    private static readonly ObjectIdentifier Dot1dStpPortDesignatedBridge = new("1.3.6.1.2.1.17.2.15.1.8");

    // dot1dStpProtocolSpecification value 1 = unknown (STP not actually running on this bridge).
    private const int Dot1dStpProtocolUnknown = 1;

    private static readonly Dictionary<int, string> StpPortStateNames = new()
    {
        [1] = "disabled",
        [2] = "blocking",
        [3] = "listening",
        [4] = "learning",
        [5] = "forwarding",
        [6] = "broken",
    };

    // Single implicit STP instance per device — this codebase doesn't model per-VLAN
    // MSTP/PVST+ instances, so all bridge-level facts key under one synthetic bridge name.
    private const string DefaultBridgeName = "default";

    // ── Q-BRIDGE-MIB (dot1qVlan) ──────────────────────────────────────────────
    // dot1qPvid and the static VLAN table are indexed by dot1dBasePort, not ifIndex —
    // resolved to an interface via Dot1dBasePortIfIndex above.
    private static readonly ObjectIdentifier Dot1qPvid = new("1.3.6.1.2.1.17.7.1.4.5.1.1");
    private static readonly ObjectIdentifier Dot1qVlanStaticEgressPorts = new("1.3.6.1.2.1.17.7.1.4.3.1.2");
    private static readonly ObjectIdentifier Dot1qVlanStaticUntaggedPorts = new("1.3.6.1.2.1.17.7.1.4.3.1.4");

    // ifType values for loopback detection (IANAifType)
    private const int IfTypeLoopback = 24;
    private const int IfTypeSoftware = 53; // propVirtual

    // entPhysicalClass values
    private static readonly Dictionary<int, string> EntityClassNames = new()
    {
        [1] = "other",
        [2] = "unknown",
        [3] = "chassis",
        [4] = "backplane",
        [5] = "container",
        [6] = "powerSupply",
        [7] = "fan",
        [8] = "sensor",
        [9] = "module",
        [10] = "port",
        [11] = "stack",
        [12] = "cpu",
    };

    public string CollectorType => "snmp";

    public bool CanCollect(Target target) =>
        target.CollectorType != null && target.CollectorType.Equals("snmp", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<Fact>> CollectAsync(
        Target target,
        ICollectionContext context,
        CancellationToken ct
    )
    {
        string community = "public";
        if (target.Credentials is SnmpCredentials snmpCreds)
        {
            community = snmpCreds.Community;
        }

        int port = target.Properties.GetInt("port", 161);
        int timeout = target.Properties.GetInt("timeout_ms", 5000);

        try
        {
            IPEndPoint endpoint = new(IPAddress.Parse(target.Endpoint), port);
            OctetString communityString = new(community);

            // Collect interface tables first — MAC from ifTable is needed as fingerprint.
            Dictionary<int, IfRow> ifRows = await CollectIfTableAsync(endpoint, communityString, timeout, ct);
            await EnrichIfXTableAsync(ifRows, endpoint, communityString, timeout, ct);
            await EnrichIfIpAddressesAsync(ifRows, endpoint, communityString, timeout, ct);

            // Engine ID — stable RFC 3411 identifier; may be absent on old firmware.
            string engineId = await CollectEngineIdAsync(endpoint, communityString, timeout, ct);

            // sysGroup scalars
            IList<Variable> sysResult = await Task.Run(
                () => Messenger.Get(
                    VersionCode.V2,
                    endpoint,
                    communityString,
                    [
                        new Variable(SysDescr),
                        new Variable(SysObjectID),
                        new Variable(SysUpTime),
                        new Variable(SysContact),
                        new Variable(SysName),
                        new Variable(SysLocation),
                    ],
                    timeout
                ),
                ct
            );

            string sysDescr = GetSysValue(sysResult, SysDescr);
            string sysObjectId = GetSysValue(sysResult, SysObjectID);
            string sysContact = GetSysValue(sysResult, SysContact);
            string sysName = GetSysValue(sysResult, SysName);
            string sysLocation = GetSysValue(sysResult, SysLocation);

            // Fingerprints: Engine ID (stable) + first non-loopback MAC (secondary).
            List<Fingerprint> fingerprints = new(2);
            if (!string.IsNullOrEmpty(engineId))
            {
                fingerprints.Add(new Fingerprint(FingerprintType.SnmpEngineId, engineId));
            }

            string? firstMac = ExtractFirstMac(ifRows);
            if (!string.IsNullOrEmpty(firstMac))
            {
                fingerprints.Add(new Fingerprint(FingerprintType.Mac, firstMac));
            }

            string? vendor = ResolveVendor(sysObjectId);

            DeviceIdentity identity = new(
                Fingerprints: fingerprints,
                Kind: "network-device",
                Vendor: vendor,
                OsFamily: null,
                OsVersion: null
            );

            string deviceId = await context.RegisterProbeAsync(identity, ct);

            List<Fact> facts =
            [
                Fact.Create(FactPaths.SnmpSysDescr, [deviceId], sysDescr),
                Fact.Create(FactPaths.SnmpSysName, [deviceId], sysName),
                Fact.Create(FactPaths.SnmpSysLocation, [deviceId], sysLocation),
                Fact.Create(FactPaths.SnmpSysContact, [deviceId], sysContact),
                Fact.Create(FactPaths.SnmpSysObjectID, [deviceId], sysObjectId),
            ];

            // ── sysGroup facts ────────────────────────────────────────────────

            facts.AddIfPresent(FactPaths.SnmpEngineId, [deviceId], engineId);

            // ── Interface facts ───────────────────────────────────────────────
            EmitInterfaceFacts(ifRows, deviceId, facts);

            // ── ARP / neighbor cache ──────────────────────────────────────────
            await EmitArpFactsAsync(endpoint, communityString, timeout, deviceId, facts, ct);

            // ── ENTITY-MIB hardware inventory ─────────────────────────────────
            await EmitEntityFactsAsync(endpoint, communityString, timeout, deviceId, facts, ct);

            // ── LLDP topology neighbors ───────────────────────────────────────
            await EmitLldpFactsAsync(endpoint, communityString, timeout, deviceId, facts, ct);

            // ── VLAN membership + spanning tree ───────────────────────────────
            await EmitVlanAndStpFactsAsync(endpoint, communityString, timeout, deviceId, ifRows, facts, ct);

            return facts;
        }
        catch (Exception ex)
        {
            SnmpCollectorLog.CollectionFailed(Log, ex, target.Endpoint);
            return Array.Empty<Fact>();
        }
    }

    // ── SNMP walk (review D20) ─────────────────────────────────────────────────

    /// <summary>
    /// Walks <paramref name="oid" />'s subtree and returns the collected variables. Throws on
    /// failure (timeout, unreachable device, etc.) — callers decide per-call whether that means
    /// "skip this column" or "abandon this table", so the try/catch stays at the call site.
    /// </summary>
    private static Task<List<Variable>> WalkAsync(
        IPEndPoint endpoint,
        OctetString community,
        ObjectIdentifier oid,
        int timeout,
        CancellationToken ct
    )
    {
        List<Variable> result = new();
        return Task.Run(
            () =>
            {
                Messenger.Walk(VersionCode.V2, endpoint, community, oid, result, timeout, WalkMode.WithinSubtree);
                return result;
            },
            ct
        );
    }

    // ── Interface collection ──────────────────────────────────────────────────

    private static async Task<Dictionary<int, IfRow>> CollectIfTableAsync(
        IPEndPoint endpoint,
        OctetString community,
        int timeout,
        CancellationToken ct
    )
    {
        Dictionary<int, IfRow> rows = new();

        ObjectIdentifier[] columns =
            [IfDescr, IfType, IfMtu, IfSpeed, IfPhysAddress, IfAdminStatus, IfOperStatus, IfInOctets, IfOutOctets];
        foreach (ObjectIdentifier col in columns)
        {
            List<Variable> result;
            try
            {
                result = await WalkAsync(endpoint, community, col, timeout, ct);
            }
            catch (Exception ex)
            {
                string oidStr = col.ToString();
                SnmpCollectorLog.WalkColumnFailed(Log, ex, oidStr);
                continue;
            }

            foreach (Variable v in result)
            {
                int idx = ExtractLastIndex(v.Id.ToString());
                if (idx <= 0)
                {
                    continue;
                }

                if (!rows.TryGetValue(idx, out IfRow? row))
                {
                    row = new IfRow
                    {
                        Index = idx,
                    };
                    rows[idx] = row;
                }

                string baseOid = GetBaseOid(v.Id.ToString());
                if (baseOid == IfDescr.ToString())
                {
                    row.Descr = v.Data.ToString();
                }
                else if (baseOid == IfType.ToString())
                {
                    if (int.TryParse(v.Data.ToString(), out int t))
                    {
                        row.Type = t;
                    }
                }
                else if (baseOid == IfMtu.ToString())
                {
                    if (long.TryParse(v.Data.ToString(), out long mtu))
                    {
                        row.Mtu = mtu;
                    }
                }
                else if (baseOid == IfSpeed.ToString())
                {
                    if (long.TryParse(v.Data.ToString(), out long spd))
                    {
                        row.SpeedBps = spd;
                    }
                }
                else if (baseOid == IfPhysAddress.ToString())
                {
                    if (v.Data is OctetString octet)
                    {
                        byte[] bytes = octet.GetRaw();
                        if (bytes.Length == 6)
                        {
                            row.Mac = MacFormat.FromBytes(bytes);
                        }
                    }
                }
                else if (baseOid == IfAdminStatus.ToString())
                {
                    row.AdminStatus = v.Data.ToString() == "1" ? "up" : "down";
                }
                else if (baseOid == IfOperStatus.ToString())
                {
                    row.OperStatus = v.Data.ToString() switch
                    {
                        "1" => "up",
                        "2" => "down",
                        "3" => "testing",
                        "5" => "dormant",
                        "6" => "notPresent",
                        "7" => "lowerLayerDown",
                        _ => "unknown",
                    };
                }
                else if (baseOid == IfInOctets.ToString())
                {
                    if (long.TryParse(v.Data.ToString(), out long val))
                    {
                        row.RxBytes32 = val;
                    }
                }
                else if (baseOid == IfOutOctets.ToString())
                {
                    if (long.TryParse(v.Data.ToString(), out long val))
                    {
                        row.TxBytes32 = val;
                    }
                }
            }
        }

        return rows;
    }

    private static async Task EnrichIfXTableAsync(
        Dictionary<int, IfRow> rows,
        IPEndPoint endpoint,
        OctetString community,
        int timeout,
        CancellationToken ct
    )
    {
        ObjectIdentifier[] columns = [IfName, IfAlias, IfHighSpeed, IfHCInOctets, IfHCOutOctets];
        foreach (ObjectIdentifier col in columns)
        {
            List<Variable> result;
            try
            {
                result = await WalkAsync(endpoint, community, col, timeout, ct);
            }
            catch (Exception ex)
            {
                string oidStr = col.ToString();
                SnmpCollectorLog.WalkColumnFailed(Log, ex, oidStr);
                continue;
            }

            foreach (Variable v in result)
            {
                int idx = ExtractLastIndex(v.Id.ToString());
                if (idx <= 0 || !rows.TryGetValue(idx, out IfRow? row))
                {
                    continue;
                }

                string baseOid = GetBaseOid(v.Id.ToString());
                if (baseOid == IfName.ToString())
                {
                    row.Name = v.Data.ToString();
                }
                else if (baseOid == IfAlias.ToString())
                {
                    row.Alias = v.Data.ToString();
                }
                else if (baseOid == IfHighSpeed.ToString())
                {
                    // IfHighSpeed is in Mbps; convert to bps only if > 0 and > 32-bit IfSpeed cap.
                    if (long.TryParse(v.Data.ToString(), out long mbps) && mbps > 0)
                    {
                        row.HighSpeedBps = mbps * 1_000_000L;
                    }
                }
                else if (baseOid == IfHCInOctets.ToString())
                {
                    if (long.TryParse(v.Data.ToString(), out long val) && val > 0)
                    {
                        row.RxBytesHC = val;
                    }
                }
                else if (baseOid == IfHCOutOctets.ToString())
                {
                    if (long.TryParse(v.Data.ToString(), out long val) && val > 0)
                    {
                        row.TxBytesHC = val;
                    }
                }
            }
        }
    }

    private static async Task EnrichIfIpAddressesAsync(
        Dictionary<int, IfRow> rows,
        IPEndPoint endpoint,
        OctetString community,
        int timeout,
        CancellationToken ct
    )
    {
        // ipAddrTable.ipAdEntIfIndex maps IP address (last 4 OID components) → ifIndex.
        List<Variable> result;
        try
        {
            result = await WalkAsync(endpoint, community, IpAdEntIfIndex, timeout, ct);
        }
        catch (Exception ex)
        {
            SnmpCollectorLog.IpAddrTableWalkFailed(Log, ex);
            return;
        }

        foreach (Variable v in result)
        {
            if (!int.TryParse(v.Data.ToString(), out int ifIdx) || !rows.TryGetValue(ifIdx, out IfRow? row))
            {
                continue;
            }

            // OID suffix is the IP address: 1.3.6.1.2.1.4.20.1.2.{a}.{b}.{c}.{d}
            string oid = v.Id.ToString();
            int prefixLen = IpAdEntIfIndex.ToString().Length + 1; // +1 for the dot
            if (oid.Length > prefixLen)
            {
                string ipStr = oid[prefixLen..];
                if (IPAddress.TryParse(ipStr, out _))
                {
                    row.IPv4Addresses.Add(ipStr);
                }
            }
        }
    }

    private static void EmitInterfaceFacts(Dictionary<int, IfRow> rows, string deviceId, List<Fact> facts)
    {
        foreach (IfRow row in rows.Values)
        {
            // Skip loopback and virtual interfaces — they add noise, not value.
            if (row.Type == IfTypeLoopback || row.Type == IfTypeSoftware)
            {
                continue;
            }

            // Key: MAC when available (stable, cross-collector join); index-based fallback.
            bool hasMac = !string.IsNullOrEmpty(row.Mac) && row.Mac != "00:00:00:00:00:00";
            string ifKey = hasMac ? row.Mac! : $"snmp-if-{row.Index}";

            // Prefer ifName (short) over ifDescr (verbose).
            string displayName = row.Name ?? row.Descr ?? ifKey;
            facts.Add(Fact.Create(FactPaths.InterfaceName, [deviceId, ifKey], displayName));

            if (hasMac)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceMAC, [deviceId, ifKey], row.Mac!));
            }

            facts.AddIfPresent(FactPaths.InterfaceAlias, [deviceId, ifKey], row.Alias);

            if (row.Mtu.HasValue)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceMTU, [deviceId, ifKey], row.Mtu.Value));
            }

            // Prefer 64-bit HC speed (HighSpeedBps), fall back to 32-bit IfSpeed.
            long? speedBps = row.HighSpeedBps ?? (row.SpeedBps > 0 ? row.SpeedBps : null);
            if (speedBps.HasValue)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceSpeedBps, [deviceId, ifKey], speedBps.Value));
            }

            if (row.AdminStatus != null)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceAdminStatus, [deviceId, ifKey], row.AdminStatus));
            }

            if (row.OperStatus != null)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceOperStatus, [deviceId, ifKey], row.OperStatus));
            }

            bool isUp = row.OperStatus == "up";
            facts.Add(Fact.Create(FactPaths.InterfaceUp, [deviceId, ifKey], isUp));

            // Prefer 64-bit HC counters; fall back to 32-bit if HC unavailable.
            long? rxBytes = row.RxBytesHC ?? row.RxBytes32;
            long? txBytes = row.TxBytesHC ?? row.TxBytes32;
            if (rxBytes.HasValue)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceRxBytes, [deviceId, ifKey], rxBytes.Value));
            }

            if (txBytes.HasValue)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceTxBytes, [deviceId, ifKey], txBytes.Value));
            }

            // IP addresses bound to this interface.
            foreach (string ip in row.IPv4Addresses)
            {
                facts.Add(Fact.Create(FactPaths.InterfaceIPv4, [deviceId, ifKey], ip));
            }
        }
    }

    // ── ARP table ─────────────────────────────────────────────────────────────

    private static async Task EmitArpFactsAsync(
        IPEndPoint endpoint,
        OctetString community,
        int timeout,
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // ipNetToMediaPhysAddr keyed by {ifIndex}.{a}.{b}.{c}.{d}
        List<Variable> physResult;
        List<Variable> typeResult;
        try
        {
            physResult = await WalkAsync(endpoint, community, IpNetToMediaPhysAddr, timeout, ct);
            typeResult = await WalkAsync(endpoint, community, IpNetToMediaType, timeout, ct);
        }
        catch (Exception ex)
        {
            SnmpCollectorLog.ArpTableWalkFailed(Log, ex);
            return;
        }

        // Build type lookup by OID suffix (ifIndex.a.b.c.d)
        Dictionary<string, int> typeByKey = new(StringComparer.Ordinal);
        string typePrefix = IpNetToMediaType + ".";
        foreach (Variable v in typeResult)
        {
            string oid = v.Id.ToString();
            if (oid.StartsWith(typePrefix, StringComparison.Ordinal) && int.TryParse(v.Data.ToString(), out int t))
            {
                typeByKey[oid[typePrefix.Length..]] = t;
            }
        }

        string physPrefix = IpNetToMediaPhysAddr + ".";
        foreach (Variable v in physResult)
        {
            string oid = v.Id.ToString();
            if (!oid.StartsWith(physPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string suffix = oid[physPrefix.Length..]; // "{ifIndex}.{a}.{b}.{c}.{d}"

            // Type 2 = invalid; skip stale entries.
            if (typeByKey.TryGetValue(suffix, out int entryType) && entryType == 2)
            {
                continue;
            }

            // Extract IP from last 4 components of suffix.
            int firstDot = suffix.IndexOf('.', StringComparison.Ordinal);
            if (firstDot < 0 || firstDot + 1 >= suffix.Length)
            {
                continue;
            }

            string ipStr = suffix[(firstDot + 1)..];
            if (!IPAddress.TryParse(ipStr, out _))
            {
                continue;
            }

            if (v.Data is not OctetString octet)
            {
                continue;
            }

            byte[] bytes = octet.GetRaw();
            if (bytes.Length != 6 || bytes.All(b => b == 0))
            {
                continue;
            }

            string mac = MacFormat.FromBytes(bytes);

            // Emit as a discovered neighbor — the materializer will create the device record.
            facts.Add(Fact.Create(FactPaths.DiscoveredMAC, [deviceId, ipStr], mac));
            facts.Add(Fact.Create(FactPaths.DiscoveredSources, [deviceId, ipStr], "snmp-arp"));
        }
    }

    // ── ENTITY-MIB ────────────────────────────────────────────────────────────

    private static async Task EmitEntityFactsAsync(
        IPEndPoint endpoint,
        OctetString community,
        int timeout,
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        Dictionary<int, EntityRow> entities = new();

        ObjectIdentifier[] columns =
        [
            EntPhysicalDescr, EntPhysicalClass, EntPhysicalName,
            EntPhysicalHwRev, EntPhysicalFwRev, EntPhysicalSwRev,
            EntPhysicalSerialNum, EntPhysicalMfgName, EntPhysicalModelName, EntPhysicalIsFRU,
        ];

        foreach (ObjectIdentifier col in columns)
        {
            List<Variable> result;
            try
            {
                result = await WalkAsync(endpoint, community, col, timeout, ct);
            }
            catch (Exception ex)
            {
                string oidStr = col.ToString();
                SnmpCollectorLog.WalkColumnFailed(Log, ex, oidStr);
                continue;
            }

            foreach (Variable v in result)
            {
                int idx = ExtractLastIndex(v.Id.ToString());
                if (idx <= 0)
                {
                    continue;
                }

                if (!entities.TryGetValue(idx, out EntityRow? row))
                {
                    row = new EntityRow
                    {
                        Index = idx,
                    };
                    entities[idx] = row;
                }

                string baseOid = GetBaseOid(v.Id.ToString());
                if (baseOid == EntPhysicalDescr.ToString())
                {
                    row.Description = v.Data.ToString();
                }
                else if (baseOid == EntPhysicalClass.ToString())
                {
                    if (int.TryParse(v.Data.ToString(), out int cls))
                    {
                        row.Class = cls;
                    }
                }
                else if (baseOid == EntPhysicalName.ToString())
                {
                    row.Name = v.Data.ToString();
                }
                else if (baseOid == EntPhysicalHwRev.ToString())
                {
                    row.HwRev = v.Data.ToString();
                }
                else if (baseOid == EntPhysicalFwRev.ToString())
                {
                    row.FwRev = v.Data.ToString();
                }
                else if (baseOid == EntPhysicalSwRev.ToString())
                {
                    row.SwRev = v.Data.ToString();
                }
                else if (baseOid == EntPhysicalSerialNum.ToString())
                {
                    row.Serial = v.Data.ToString();
                }
                else if (baseOid == EntPhysicalMfgName.ToString())
                {
                    row.Manufacturer = v.Data.ToString();
                }
                else if (baseOid == EntPhysicalModelName.ToString())
                {
                    row.Model = v.Data.ToString();
                }
                else if (baseOid == EntPhysicalIsFRU.ToString())
                {
                    row.IsFRU = v.Data.ToString() == "1";
                }
            }
        }

        foreach (EntityRow row in entities.Values)
        {
            // Skip meaningless or purely-container entries.
            if (string.IsNullOrWhiteSpace(row.Description) && string.IsNullOrWhiteSpace(row.Model))
            {
                continue;
            }

            string slotKey = $"entity-{row.Index}";

            facts.AddIfPresent(FactPaths.HwComponentDescription, [deviceId, slotKey], row.Description);

            if (row.Class.HasValue && EntityClassNames.TryGetValue(row.Class.Value, out string? className))
            {
                facts.Add(Fact.Create(FactPaths.HwComponentClass, [deviceId, slotKey], className));
            }

            facts.AddIfPresent(FactPaths.HwComponentSlot, [deviceId, slotKey], row.Name);

            facts.AddIfPresent(FactPaths.HwComponentVendor, [deviceId, slotKey], row.Manufacturer);

            facts.AddIfPresent(FactPaths.HwComponentModel, [deviceId, slotKey], row.Model);

            facts.AddIfPresent(FactPaths.HwComponentSerial, [deviceId, slotKey], row.Serial);

            // Use firmware revision if present; fall back to SW rev.
            string? firmware = NullIfEmpty(row.FwRev) ?? NullIfEmpty(row.SwRev) ?? NullIfEmpty(row.HwRev);
            if (firmware != null)
            {
                facts.Add(Fact.Create(FactPaths.HwComponentFirmware, [deviceId, slotKey], firmware));
            }

            if (row.IsFRU.HasValue)
            {
                facts.Add(Fact.Create(FactPaths.HwComponentIsFru, [deviceId, slotKey], row.IsFRU.Value));
            }
        }
    }

    // ── LLDP neighbors ────────────────────────────────────────────────────────

    private static async Task EmitLldpFactsAsync(
        IPEndPoint endpoint,
        OctetString community,
        int timeout,
        string deviceId,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // lldpLocPortTable resolves each local port number to a human-readable label,
        // so a neighbor row can be tied to a specific local interface instead of floating
        // as a bare remote string.
        Dictionary<string, string> localPortLabelByNum = await CollectLldpLocalPortLabelsAsync(
            endpoint,
            community,
            timeout,
            ct
        );

        // lldpRemTable is indexed by {timeMark}.{localPort}.{remIndex} — we only need chassisId and sysName.
        Dictionary<string, LldpRemRow> lldpRows = new(StringComparer.Ordinal);

        ObjectIdentifier[] columns = [LldpRemChassisIdSubtype, LldpRemChassisId, LldpRemPortId, LldpRemSysName];
        foreach (ObjectIdentifier col in columns)
        {
            List<Variable> result;
            try
            {
                result = await WalkAsync(endpoint, community, col, timeout, ct);
            }
            catch (Exception ex)
            {
                string oidStr = col.ToString();
                SnmpCollectorLog.WalkColumnFailed(Log, ex, oidStr);
                continue;
            }

            foreach (Variable v in result)
            {
                // OID: {colBase}.{timeMark}.{localPort}.{remIndex}
                // We use everything after the column prefix as a row key.
                string baseOid = GetBaseOid3(v.Id.ToString(), col.ToString());
                string rowKey = GetSuffix(v.Id.ToString(), col + ".");

                if (!lldpRows.TryGetValue(rowKey, out LldpRemRow? row))
                {
                    row = new LldpRemRow();
                    lldpRows[rowKey] = row;
                }

                if (baseOid == LldpRemChassisIdSubtype.ToString())
                {
                    if (int.TryParse(v.Data.ToString(), out int sub))
                    {
                        row.ChassisIdSubtype = sub;
                    }
                }
                else if (baseOid == LldpRemChassisId.ToString())
                {
                    // chassisId encoding depends on subtype; store raw bytes for MAC extraction.
                    if (v.Data is OctetString octet)
                    {
                        row.ChassisIdBytes = octet.GetRaw();
                        row.ChassisIdStr = v.Data.ToString();
                    }
                    else
                    {
                        row.ChassisIdStr = v.Data.ToString();
                    }
                }
                else if (baseOid == LldpRemPortId.ToString())
                {
                    row.PortId = v.Data.ToString();
                }
                else if (baseOid == LldpRemSysName.ToString())
                {
                    row.RemoteSysName = v.Data.ToString();
                }
            }
        }

        // Also collect management addresses for the neighbor IP.
        // lldpRemManAddrTable is keyed by {timeMark}.{localPort}.{remIndex}.{addrType}.{addrLen}.{addr...}
        // addrType 1 = IPv4.
        Dictionary<string, string> ipByRemKey = new(StringComparer.Ordinal);
        List<Variable> manResult;
        try
        {
            manResult = await WalkAsync(endpoint, community, LldpRemManAddrIfId, timeout, ct);
        }
        catch (Exception ex)
        {
            SnmpCollectorLog.LldpManAddrWalkFailed(Log, ex);
            manResult = [];
        }

        string manPrefix = LldpRemManAddrIfId + ".";
        foreach (Variable v in manResult)
        {
            string oid = v.Id.ToString();
            if (!oid.StartsWith(manPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // Suffix: {timeMark}.{localPort}.{remIndex}.{addrSubtype}.{addrLen}.{a}.{b}.{c}.{d}
            // addrSubtype 1 = IPv4, followed by length 4, then 4 octets.
            string suffix = oid[manPrefix.Length..];
            string[] parts = suffix.Split('.');
            if (parts.Length < 9)
            {
                continue;
            }

            // addrSubtype at index 3, addrLen at index 4.
            if (parts[3] != "1" || parts[4] != "4")
            {
                continue;
            }

            string ipStr = string.Join(".", parts[5], parts[6], parts[7], parts[8]);
            if (!IPAddress.TryParse(ipStr, out _))
            {
                continue;
            }

            // remKey is the first 3 components (timeMark.localPort.remIndex).
            string remKey = string.Join(".", parts[0], parts[1], parts[2]);
            ipByRemKey.TryAdd(remKey, ipStr);
        }

        foreach (KeyValuePair<string, LldpRemRow> kvp in lldpRows)
        {
            LldpRemRow row = kvp.Value;

            // Derive neighbor IP and MAC for the Discovered[] emission.
            string? neighborIp = null;
            string? neighborMac = null;

            // Extract remKey (first 3 components of the row key).
            string[] keyParts = kvp.Key.Split('.');
            if (keyParts.Length >= 3)
            {
                string remKey = string.Join(".", keyParts[0], keyParts[1], keyParts[2]);
                ipByRemKey.TryGetValue(remKey, out neighborIp);
            }

            // ChassisIdSubtype 4 = macAddress (6 bytes).
            if (row.ChassisIdSubtype == 4
             && row.ChassisIdBytes != null
             && row.ChassisIdBytes.Length == 6
             && !row.ChassisIdBytes.All(b => b == 0))
            {
                neighborMac = MacFormat.FromBytes(row.ChassisIdBytes);
            }
            // ChassisIdSubtype 5 = networkAddress; if IPv4, extract.
            else if (row.ChassisIdSubtype == 5
             && row.ChassisIdBytes != null
             && row.ChassisIdBytes.Length == 5
             && row.ChassisIdBytes[0] == 1)
            {
                neighborIp ??= string.Join(
                    ".",
                    row.ChassisIdBytes[1],
                    row.ChassisIdBytes[2],
                    row.ChassisIdBytes[3],
                    row.ChassisIdBytes[4]
                );
            }

            // Need at least IP or MAC to emit a useful discovered entry.
            string? key = neighborIp ?? neighborMac;
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            facts.AddIfPresent(FactPaths.DiscoveredMAC, [deviceId, key], neighborMac);

            facts.AddIfPresent(FactPaths.DiscoveredHostname, [deviceId, key], row.RemoteSysName);

            facts.Add(Fact.Create(FactPaths.DiscoveredSources, [deviceId, key], "snmp-lldp"));

            // ── L2 edge data: local port ↔ remote neighbor, for the L2 topology graph ──
            // keyParts[1] is the local port number (the second component of
            // "{timeMark}.{localPort}.{remIndex}"). The neighbor key intentionally excludes
            // timeMark (which changes every poll) so it stays stable across collections.
            string localPortNum = keyParts.Length >= 2 ? keyParts[1] : "0";
            string neighborKey = row.ChassisIdStr ?? key;
            string subtreeKey = $"{localPortNum}-{neighborKey}";

            string localPortLabel = localPortLabelByNum.TryGetValue(localPortNum, out string? label)
                ? label
                : localPortNum;

            facts.Add(Fact.Create(FactPaths.NeighborLocalPort, [deviceId, subtreeKey], localPortLabel));
            facts.AddIfPresent(FactPaths.NeighborRemoteChassisId, [deviceId, subtreeKey], row.ChassisIdStr);
            facts.AddIfPresent(FactPaths.NeighborRemotePortId, [deviceId, subtreeKey], row.PortId);
            facts.AddIfPresent(FactPaths.NeighborRemoteSysName, [deviceId, subtreeKey], row.RemoteSysName);
            facts.AddIfPresent(FactPaths.NeighborRemoteMac, [deviceId, subtreeKey], neighborMac);
            facts.AddIfPresent(FactPaths.NeighborRemoteIp, [deviceId, subtreeKey], neighborIp);
            facts.Add(Fact.Create(FactPaths.NeighborProtocol, [deviceId, subtreeKey], "lldp"));
        }
    }

    // ── lldpLocPortTable (local-port resolution) ─────────────────────────────

    private static async Task<Dictionary<string, string>> CollectLldpLocalPortLabelsAsync(
        IPEndPoint endpoint,
        OctetString community,
        int timeout,
        CancellationToken ct
    )
    {
        Dictionary<string, string> descByPortNum = new(StringComparer.Ordinal);
        Dictionary<string, string> idByPortNum = new(StringComparer.Ordinal);
        Dictionary<string, int> idSubtypeByPortNum = new(StringComparer.Ordinal);

        (ObjectIdentifier col, Action<string, Variable> assign)[] columns =
        [
            (LldpLocPortDesc, (portNum, v) => descByPortNum[portNum] = v.Data.ToString()),
            (LldpLocPortId, (portNum, v) => idByPortNum[portNum] = v.Data.ToString()),
            (
                LldpLocPortIdSubtype,
                (portNum, v) =>
                {
                    if (int.TryParse(v.Data.ToString(), out int sub))
                    {
                        idSubtypeByPortNum[portNum] = sub;
                    }
                }
            ),
        ];

        foreach ((ObjectIdentifier col, Action<string, Variable> assign) in columns)
        {
            List<Variable> result;
            try
            {
                result = await WalkAsync(endpoint, community, col, timeout, ct);
            }
            catch (Exception ex)
            {
                string oidStr = col.ToString();
                SnmpCollectorLog.WalkColumnFailed(Log, ex, oidStr);
                continue;
            }

            foreach (Variable v in result)
            {
                // lldpLocPortTable is indexed by lldpLocPortNum alone (single-component instance suffix).
                int portNum = ExtractLastIndex(v.Id.ToString());
                if (portNum <= 0)
                {
                    continue;
                }

                assign(portNum.ToString(), v);
            }
        }

        return BuildLocalPortLabels(descByPortNum, idByPortNum, idSubtypeByPortNum);
    }

    /// <summary>
    /// Pure resolution logic for lldpLocPortTable: prefer lldpLocPortDesc (per RFC, equal to
    /// ifDescr when the port has an ifIndex); fall back to lldpLocPortId when its subtype is
    /// interfaceName (5). Split out from the SNMP walk itself so it's unit-testable without a
    /// live agent — see <see cref="CollectLldpLocalPortLabelsAsync"/>.
    /// </summary>
    private static Dictionary<string, string> BuildLocalPortLabels(
        Dictionary<string, string> descByPortNum,
        Dictionary<string, string> idByPortNum,
        Dictionary<string, int> idSubtypeByPortNum
    )
    {
        Dictionary<string, string> labelByPortNum = new(StringComparer.Ordinal);
        foreach (string portNum in descByPortNum.Keys.Concat(idByPortNum.Keys).Distinct())
        {
            if (descByPortNum.TryGetValue(portNum, out string? desc) && !string.IsNullOrWhiteSpace(desc))
            {
                labelByPortNum[portNum] = desc;
            }
            else if (idSubtypeByPortNum.TryGetValue(portNum, out int subtype)
             && subtype == LldpPortIdSubtypeInterfaceName
             && idByPortNum.TryGetValue(portNum, out string? id)
             && !string.IsNullOrWhiteSpace(id))
            {
                labelByPortNum[portNum] = id;
            }
        }

        return labelByPortNum;
    }

    // ── VLAN membership (Q-BRIDGE-MIB) + spanning tree (BRIDGE-MIB) ──────────

    private static async Task EmitVlanAndStpFactsAsync(
        IPEndPoint endpoint,
        OctetString community,
        int timeout,
        string deviceId,
        Dictionary<int, IfRow> ifRows,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        // dot1dBasePort → ifIndex, needed because every dot1dBase/dot1dStp/dot1qVlan table
        // below is indexed by dot1dBasePort, not ifIndex.
        Dictionary<int, int> ifIndexByBasePort = new();
        try
        {
            List<Variable> result = await WalkAsync(endpoint, community, Dot1dBasePortIfIndex, timeout, ct);
            foreach (Variable v in result)
            {
                int basePort = ExtractLastIndex(v.Id.ToString());
                if (basePort > 0 && int.TryParse(v.Data.ToString(), out int ifIndex))
                {
                    ifIndexByBasePort[basePort] = ifIndex;
                }
            }
        }
        catch (Exception ex)
        {
            string oidStr = Dot1dBasePortIfIndex.ToString();
            SnmpCollectorLog.WalkColumnFailed(Log, ex, oidStr);
            return; // nothing below is resolvable to an interface without this mapping.
        }

        string? IfKeyForBasePort(int basePort) =>
            ifIndexByBasePort.TryGetValue(basePort, out int ifIndex) && ifRows.TryGetValue(ifIndex, out IfRow? row)
                ? (!string.IsNullOrEmpty(row.Mac) && row.Mac != "00:00:00:00:00:00" ? row.Mac : $"snmp-if-{ifIndex}")
                : null;

        await EmitVlanMembershipAsync(endpoint, community, timeout, deviceId, IfKeyForBasePort, facts, ct);
        await EmitStpFactsAsync(endpoint, community, timeout, deviceId, IfKeyForBasePort, facts, ct);
    }

    private static async Task EmitVlanMembershipAsync(
        IPEndPoint endpoint,
        OctetString community,
        int timeout,
        string deviceId,
        Func<int, string?> ifKeyForBasePort,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        Dictionary<int, int> pvidByBasePort = new();
        try
        {
            List<Variable> pvidResult = await WalkAsync(endpoint, community, Dot1qPvid, timeout, ct);
            foreach (Variable v in pvidResult)
            {
                int basePort = ExtractLastIndex(v.Id.ToString());
                if (basePort > 0 && int.TryParse(v.Data.ToString(), out int pvid))
                {
                    pvidByBasePort[basePort] = pvid;
                }
            }
        }
        catch (Exception ex)
        {
            string oidStr = Dot1qPvid.ToString();
            SnmpCollectorLog.WalkColumnFailed(Log, ex, oidStr);
        }

        // vlanId → tagged base-port numbers (egress minus untagged), built once across all VLANs.
        Dictionary<int, HashSet<int>> egressByVlan = new();
        Dictionary<int, HashSet<int>> untaggedByVlan = new();
        try
        {
            List<Variable> egressResult = await WalkAsync(
                endpoint,
                community,
                Dot1qVlanStaticEgressPorts,
                timeout,
                ct
            );
            foreach (Variable v in egressResult)
            {
                int vlanId = ExtractLastIndex(v.Id.ToString());
                if (vlanId > 0 && v.Data is OctetString octet)
                {
                    egressByVlan[vlanId] = DecodePortList(octet.GetRaw());
                }
            }

            List<Variable> untaggedResult = await WalkAsync(
                endpoint,
                community,
                Dot1qVlanStaticUntaggedPorts,
                timeout,
                ct
            );
            foreach (Variable v in untaggedResult)
            {
                int vlanId = ExtractLastIndex(v.Id.ToString());
                if (vlanId > 0 && v.Data is OctetString octet)
                {
                    untaggedByVlan[vlanId] = DecodePortList(octet.GetRaw());
                }
            }
        }
        catch (Exception ex)
        {
            string oidStr = Dot1qVlanStaticEgressPorts.ToString();
            SnmpCollectorLog.WalkColumnFailed(Log, ex, oidStr);
        }

        Dictionary<int, List<int>> taggedVlansByBasePort = BuildTaggedVlanMembership(egressByVlan, untaggedByVlan);

        foreach (int basePort in pvidByBasePort.Keys.Concat(taggedVlansByBasePort.Keys).Distinct())
        {
            string? ifKey = ifKeyForBasePort(basePort);
            if (ifKey == null)
            {
                continue;
            }

            if (pvidByBasePort.TryGetValue(basePort, out int pvid))
            {
                facts.Add(Fact.Create(FactPaths.InterfaceVlanId, [deviceId, ifKey], pvid));
            }

            if (taggedVlansByBasePort.TryGetValue(basePort, out List<int>? tagged) && tagged.Count > 0)
            {
                facts.Add(
                    Fact.Create(
                        FactPaths.InterfaceTaggedVlans,
                        [deviceId, ifKey],
                        string.Join(",", tagged.OrderBy(v => v))
                    )
                );
            }
        }
    }

    /// <summary>
    /// Pure logic: a base port is "tagged" on a VLAN when it's an egress member but not an
    /// untagged member (dot1qVlanStaticEgressPorts minus dot1qVlanStaticUntaggedPorts) — the
    /// standard Q-BRIDGE-MIB derivation for trunk membership.
    /// </summary>
    private static Dictionary<int, List<int>> BuildTaggedVlanMembership(
        Dictionary<int, HashSet<int>> egressByVlan,
        Dictionary<int, HashSet<int>> untaggedByVlan
    )
    {
        Dictionary<int, List<int>> taggedVlansByBasePort = new();
        foreach ((int vlanId, HashSet<int> egressPorts) in egressByVlan)
        {
            HashSet<int> untaggedPorts = untaggedByVlan.TryGetValue(vlanId, out HashSet<int>? u) ? u : [];
            foreach (int basePort in egressPorts)
            {
                if (untaggedPorts.Contains(basePort))
                {
                    continue;
                }

                if (!taggedVlansByBasePort.TryGetValue(basePort, out List<int>? vlans))
                {
                    vlans = [];
                    taggedVlansByBasePort[basePort] = vlans;
                }

                vlans.Add(vlanId);
            }
        }

        return taggedVlansByBasePort;
    }

    /// <summary>
    /// Decodes a Q-BRIDGE-MIB PortList bitmap (RFC 2674): each octet covers 8 ports, most
    /// significant bit = lowest port number in that octet (octet 0 bit 0 = port 1, bit 1 = port
    /// 2, ... octet 1 bit 0 = port 9, etc.).
    /// </summary>
    private static HashSet<int> DecodePortList(byte[] raw)
    {
        HashSet<int> ports = [];
        for (int octetIdx = 0; octetIdx < raw.Length; octetIdx++)
        {
            byte b = raw[octetIdx];
            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (0x80 >> bit)) != 0)
                {
                    ports.Add((octetIdx * 8) + bit + 1);
                }
            }
        }

        return ports;
    }

    private static async Task EmitStpFactsAsync(
        IPEndPoint endpoint,
        OctetString community,
        int timeout,
        string deviceId,
        Func<int, string?> ifKeyForBasePort,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        IList<Variable> scalars;
        try
        {
            scalars = await Task.Run(
                () => Messenger.Get(
                    VersionCode.V2,
                    endpoint,
                    community,
                    [
                        new Variable(Dot1dBaseBridgeAddress),
                        new Variable(Dot1dStpProtocolSpecification),
                        new Variable(Dot1dStpPriority),
                        new Variable(Dot1dStpDesignatedRoot),
                        new Variable(Dot1dStpRootCost),
                        new Variable(Dot1dStpRootPort),
                    ],
                    timeout
                ),
                ct
            );
        }
        catch (Exception ex)
        {
            SnmpCollectorLog.WalkColumnFailed(Log, ex, "dot1dStp scalars");
            return; // no bridge-level STP data available; skip the per-port walk too (no root to compare against).
        }

        byte[]? bridgeMac = GetOctetValue(scalars, Dot1dBaseBridgeAddress);
        int? protocolSpec = GetIntValue(scalars, Dot1dStpProtocolSpecification);
        int? priority = GetIntValue(scalars, Dot1dStpPriority);
        byte[]? designatedRoot = GetOctetValue(scalars, Dot1dStpDesignatedRoot);
        int? rootCost = GetIntValue(scalars, Dot1dStpRootCost);
        int? rootPortNum = GetIntValue(scalars, Dot1dStpRootPort);

        if (bridgeMac == null || bridgeMac.Length != 6 || priority == null)
        {
            return; // device doesn't implement dot1dStp — nothing meaningful to report.
        }

        string bridgeId = FormatBridgeId(priority.Value, bridgeMac);
        string? rootId = designatedRoot is { Length: 8 } ? FormatBridgeId(
            (designatedRoot[0] << 8) | designatedRoot[1],
            designatedRoot[2..8]
        ) : null;

        bool stpEnabled = protocolSpec.HasValue && protocolSpec.Value != Dot1dStpProtocolUnknown;

        facts.Add(Fact.Create(FactPaths.BridgeStpEnabled, [deviceId, DefaultBridgeName], stpEnabled));
        facts.Add(Fact.Create(FactPaths.BridgeId, [deviceId, DefaultBridgeName], bridgeId));
        facts.AddIfPresent(FactPaths.BridgeRootId, [deviceId, DefaultBridgeName], rootId);
        if (rootCost.HasValue)
        {
            facts.Add(Fact.Create(FactPaths.BridgeRootPathCost, [deviceId, DefaultBridgeName], rootCost.Value));
        }

        // Root port 0 means this bridge IS the root (no upstream port toward it).
        string? rootPortIfKey = rootPortNum is > 0 ? ifKeyForBasePort(rootPortNum.Value) : null;
        facts.AddIfPresent(FactPaths.BridgeRootPort, [deviceId, DefaultBridgeName], rootPortIfKey);

        await EmitStpPortTableAsync(endpoint, community, timeout, deviceId, ifKeyForBasePort, bridgeId, rootPortNum, facts, ct);
    }

    private static async Task EmitStpPortTableAsync(
        IPEndPoint endpoint,
        OctetString community,
        int timeout,
        string deviceId,
        Func<int, string?> ifKeyForBasePort,
        string ourBridgeId,
        int? rootPortNum,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        Dictionary<int, int> stateByBasePort = new();
        Dictionary<int, int> costByBasePort = new();
        Dictionary<int, string> designatedBridgeByBasePort = new();

        (ObjectIdentifier col, Action<int, Variable> assign)[] columns =
        [
            (
                Dot1dStpPortState,
                (basePort, v) =>
                {
                    if (int.TryParse(v.Data.ToString(), out int state))
                    {
                        stateByBasePort[basePort] = state;
                    }
                }
            ),
            (
                Dot1dStpPortPathCost,
                (basePort, v) =>
                {
                    if (int.TryParse(v.Data.ToString(), out int cost))
                    {
                        costByBasePort[basePort] = cost;
                    }
                }
            ),
            (
                Dot1dStpPortDesignatedBridge,
                (basePort, v) =>
                {
                    if (v.Data is OctetString octet && octet.GetRaw().Length == 8)
                    {
                        byte[] raw = octet.GetRaw();
                        designatedBridgeByBasePort[basePort] = FormatBridgeId((raw[0] << 8) | raw[1], raw[2..8]);
                    }
                }
            ),
        ];

        foreach ((ObjectIdentifier col, Action<int, Variable> assign) in columns)
        {
            List<Variable> result;
            try
            {
                result = await WalkAsync(endpoint, community, col, timeout, ct);
            }
            catch (Exception ex)
            {
                string oidStr = col.ToString();
                SnmpCollectorLog.WalkColumnFailed(Log, ex, oidStr);
                continue;
            }

            foreach (Variable v in result)
            {
                int basePort = ExtractLastIndex(v.Id.ToString());
                if (basePort > 0)
                {
                    assign(basePort, v);
                }
            }
        }

        foreach (int basePort in stateByBasePort.Keys)
        {
            string? ifKey = ifKeyForBasePort(basePort);
            if (ifKey == null)
            {
                continue;
            }

            int state = stateByBasePort[basePort];
            if (StpPortStateNames.TryGetValue(state, out string? stateName))
            {
                facts.Add(Fact.Create(FactPaths.InterfaceStpState, [deviceId, ifKey], stateName));
            }

            if (costByBasePort.TryGetValue(basePort, out int cost))
            {
                facts.Add(Fact.Create(FactPaths.InterfaceStpCost, [deviceId, ifKey], cost));
            }

            designatedBridgeByBasePort.TryGetValue(basePort, out string? designatedBridge);
            string? role = ComputeStpRole(rootPortNum, basePort, designatedBridge, ourBridgeId, state);
            facts.AddIfPresent(FactPaths.InterfaceStpRole, [deviceId, ifKey], role);
        }
    }

    /// <summary>
    /// Pure logic: a port is the root port if it matches dot1dStpRootPort; designated if this
    /// bridge is the segment's designated bridge; otherwise alternate (blocking) or disabled,
    /// mirroring the roles a network engineer would read off "show spanning-tree".
    /// </summary>
    private static string? ComputeStpRole(
        int? rootPortNum,
        int basePort,
        string? designatedBridge,
        string ourBridgeId,
        int state
    )
    {
        if (rootPortNum.HasValue && rootPortNum.Value == basePort)
        {
            return "root";
        }

        if (designatedBridge != null && string.Equals(designatedBridge, ourBridgeId, StringComparison.Ordinal))
        {
            return "designated";
        }

        if (StpPortStateNames.TryGetValue(state, out string? stateName) && stateName == "disabled")
        {
            return "disabled";
        }

        return "alternate";
    }

    /// <summary>Formats a BRIDGE-MIB bridge ID as "{priority}:{mac}" (hex, colon-separated MAC).</summary>
    private static string FormatBridgeId(int priority, byte[] mac) =>
        $"{priority}:{MacFormat.FromBytes(mac)}";

    private static byte[]? GetOctetValue(IList<Variable> vars, ObjectIdentifier oid)
    {
        foreach (Variable v in vars)
        {
            if (v.Id == oid && v.Data is OctetString octet)
            {
                return octet.GetRaw();
            }
        }

        return null;
    }

    private static int? GetIntValue(IList<Variable> vars, ObjectIdentifier oid)
    {
        foreach (Variable v in vars)
        {
            if (v.Id == oid && int.TryParse(v.Data.ToString(), out int val))
            {
                return val;
            }
        }

        return null;
    }

    // ── Engine ID ─────────────────────────────────────────────────────────────

    private static async Task<string> CollectEngineIdAsync(
        IPEndPoint endpoint,
        OctetString community,
        int timeout,
        CancellationToken ct
    )
    {
        try
        {
            IList<Variable> result = await Task.Run(
                () => Messenger.Get(
                    VersionCode.V2,
                    endpoint,
                    community,
                    [new Variable(SnmpEngineIdOid)],
                    timeout
                ),
                ct
            );

            foreach (Variable v in result)
            {
                if (v.Id == SnmpEngineIdOid && v.Data is OctetString eid)
                {
                    byte[] raw = eid.GetRaw();
                    if (raw.Length > 0)
                    {
                        return Convert.ToHexStringLower(raw);
                    }
                }
            }
        }
        catch (Exception ex) { SnmpCollectorLog.EngineIdQueryFailed(Log, ex); }

        return string.Empty;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? ExtractFirstMac(Dictionary<int, IfRow> rows)
    {
        foreach (IfRow row in rows.Values)
        {
            if (row.Type == IfTypeLoopback || row.Type == IfTypeSoftware)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(row.Mac) && row.Mac != "00:00:00:00:00:00")
            {
                return row.Mac;
            }
        }

        return null;
    }

    private static string GetSysValue(IList<Variable> vars, ObjectIdentifier oid)
    {
        foreach (Variable v in vars)
        {
            if (v.Id == oid)
            {
                return v.Data.ToString();
            }
        }

        return string.Empty;
    }

    private static int ExtractLastIndex(string oid)
    {
        int lastDot = oid.LastIndexOf('.');
        if (lastDot < 0)
        {
            return 0;
        }

        return int.TryParse(oid[(lastDot + 1)..], out int idx) ? idx : 0;
    }

    private static string GetBaseOid(string oid)
    {
        int lastDot = oid.LastIndexOf('.');
        return lastDot < 0 ? oid : oid[..lastDot];
    }

    // Returns the column base OID (everything up to but not including the instance suffix).
    // For LLDP table columns the suffix has multiple components, so we match against the known column prefix.
    private static string GetBaseOid3(string oid, string columnBase)
    {
        return oid.StartsWith(columnBase + ".", StringComparison.Ordinal) ? columnBase : oid;
    }

    private static string GetSuffix(string oid, string prefix)
    {
        return oid.StartsWith(prefix, StringComparison.Ordinal) ? oid[prefix.Length..] : oid;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    // sysObjectID encodes an IANA-assigned enterprise number as
    // 1.3.6.1.4.1.<enterprise-number>[.<vendor-specific path>] — extremely reliable vendor
    // signal when present. Looked up against the full IANA registry embedded with the agent
    // (see EnterpriseNumberRegistry / docs/plans/vendor-derivation-updates.md §2.5), not a
    // small hand-curated subset. Raw registrant names are returned as-is; VendorNormalizer
    // canonicalizes them downstream like every other vendor-string source.
    private const string EnterpriseNumberPrefix = "1.3.6.1.4.1.";

    private static string? ResolveVendor(string sysObjectId)
    {
        if (!sysObjectId.StartsWith(EnterpriseNumberPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        ReadOnlySpan<char> rest = sysObjectId.AsSpan(EnterpriseNumberPrefix.Length);
        int dot = rest.IndexOf('.');
        ReadOnlySpan<char> numberSpan = dot < 0 ? rest : rest[..dot];

        return int.TryParse(numberSpan, out int enterpriseNumber)
            ? EnterpriseNumberRegistry.Lookup(enterpriseNumber)
            : null;
    }

    // ── Row types ─────────────────────────────────────────────────────────────

    private sealed class IfRow
    {
        public int Index { get; set; }
        public string? Name { get; set; } // ifXTable.ifName (short form)
        public string? Descr { get; set; } // ifTable.ifDescr (verbose)
        public string? Alias { get; set; } // ifXTable.ifAlias (admin label)
        public string? Mac { get; set; }
        public int? Type { get; set; }
        public long? Mtu { get; set; }
        public long? SpeedBps { get; set; } // from 32-bit IfSpeed (in bps)
        public long? HighSpeedBps { get; set; } // from ifHighSpeed (Mbps → bps)
        public string? AdminStatus { get; set; }
        public string? OperStatus { get; set; }
        public long? RxBytes32 { get; set; } // ifInOctets (32-bit, wraps)
        public long? TxBytes32 { get; set; } // ifOutOctets (32-bit, wraps)
        public long? RxBytesHC { get; set; } // ifHCInOctets (64-bit)
        public long? TxBytesHC { get; set; } // ifHCOutOctets (64-bit)
        public List<string> IPv4Addresses { get; } = new();
    }

    private sealed class EntityRow
    {
        public int Index { get; set; }
        public string? Description { get; set; }
        public int? Class { get; set; }
        public string? Name { get; set; }
        public string? HwRev { get; set; }
        public string? FwRev { get; set; }
        public string? SwRev { get; set; }
        public string? Serial { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public bool? IsFRU { get; set; }
    }

    private sealed class LldpRemRow
    {
        public int? ChassisIdSubtype { get; set; }
        public byte[]? ChassisIdBytes { get; set; }
        public string? ChassisIdStr { get; set; }
        public string? PortId { get; set; }
        public string? RemoteSysName { get; set; }
    }
}

internal static partial class SnmpCollectorLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "SNMP collection failed for {Address}.")]
    internal static partial void CollectionFailed(ILogger logger, Exception ex, string address);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SNMP walk on OID {Oid} failed; skipping column.")]
    internal static partial void WalkColumnFailed(ILogger logger, Exception ex, string oid);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SNMP ipAddrTable walk failed.")]
    internal static partial void IpAddrTableWalkFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SNMP ARP table walk failed.")]
    internal static partial void ArpTableWalkFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SNMP LLDP management address walk failed.")]
    internal static partial void LldpManAddrWalkFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SNMP engine ID query failed.")]
    internal static partial void EngineIdQueryFailed(ILogger logger, Exception ex);
}