using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// ILocalCollector wrapper that runs all registered INetworkScanner implementations
/// across every local subnet and emits discovered neighbors as facts.
/// Facts emitted under the host device ID:
/// Device[{id}].Discovered[{ip}].MAC
/// Device[{id}].Discovered[{ip}].Hostname
/// Device[{id}].Discovered[{ip}].Sources         (comma-separated scanner names)
/// Every scanner attribute is promoted to a typed Discovered* fact (see PromoteAttributes) or a
/// list sub-dimension — there is no raw Attr[key] sink.
/// </summary>
public sealed class NetworkDiscoveryCollector : ILocalCollector
{
    private static readonly ILogger Log = AgentLog.CreateLogger<NetworkDiscoveryCollector>();
    private readonly IReadOnlyList<INetworkScanner> _scanners;
    private readonly Func<string, bool>? _scannerFilter;

    public string Name => "network-discovery";

    public bool IsSupported => _scanners.Any(s => s.IsSupported);

    /// <summary>
    /// Per-scanner stats from the most recent CollectAsync call.
    /// Empty list until at least one scan has completed.
    /// </summary>
    public IReadOnlyList<ScannerStat> LastScannerStats { get; private set; } = [];

    /// <param name="scanners">All registered network scanners.</param>
    /// <param name="scannerFilter">
    /// Optional predicate called with each scanner's class name (e.g. "ArpScanner").
    /// Returns false to skip that scanner this cycle. Defaults to all enabled.
    /// </param>
    public NetworkDiscoveryCollector(IReadOnlyList<INetworkScanner> scanners, Func<string, bool>? scannerFilter = null)
    {
        _scanners = scanners;
        _scannerFilter = scannerFilter;
    }

    public async Task<IReadOnlyList<Fact>> CollectAsync(string deviceId, CancellationToken ct)
    {
        List<NetworkScanTarget> targets = EnumerateLocalSubnets();
        if (targets.Count == 0)
        {
            return [];
        }

        List<INetworkScanner> supported = _scanners
            .Where(s => s.IsSupported && (_scannerFilter?.Invoke(s.GetType().Name) ?? true))
            .ToList();
        if (supported.Count == 0)
        {
            return [];
        }

        // Resolve the host neighbor table ONCE per cycle (warming the ARP/ND cache first) and hand
        // each scanner the neighbors on its subnet, instead of every scanner spawning its own
        // ip-neigh / arp / Get-NetNeighbor subprocess (review D1). Warming is shared with
        // ArpCollector via NeighborCacheWarmer so both read a cache populated the same way.
        await NeighborCacheWarmer.WarmAsync(
            targets.Select(t => new NeighborCacheWarmer.Subnet(t.SubnetAddress, t.PrefixLength, t.LocalAddress)).ToList(),
            ct
        );
        IReadOnlyList<Neighbor> neighbors = await NeighborTable.ResolveAsync(ct);
        targets = targets
            .Select(t => new NetworkScanTarget
            {
                SubnetAddress = t.SubnetAddress,
                PrefixLength = t.PrefixLength,
                LocalAddress = t.LocalAddress,
                InterfaceName = t.InterfaceName,
                Neighbors = neighbors
                        .Where(n => t.Contains(n.Ip) && !n.Ip.Equals(t.LocalAddress))
                        .ToList(),
            }
            )
            .ToList();

        // Run all scanner × subnet combinations concurrently, recording per-scanner stats.
        List<(INetworkScanner Scanner, Task<(IReadOnlyList<DiscoveredDevice> Devices, int DurationMs, string? Error)>
            Task)> scanTasks = new();
        foreach (NetworkScanTarget target in targets)
        {
            foreach (INetworkScanner scanner in supported)
            {
                scanTasks.Add((scanner, RunScannerAsync(scanner, target, ct)));
            }
        }

        await Task.WhenAll(scanTasks.Select(t => t.Task));

        // Merge by IP address and aggregate per-scanner stats.
        Dictionary<string, MergedDevice> merged = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, (int Devices, int TotalMs, string? FirstError)> statsAgg = new(
            StringComparer.OrdinalIgnoreCase
        );

        foreach ((INetworkScanner scanner,
            Task<(IReadOnlyList<DiscoveredDevice> Devices, int DurationMs, string? Error)> task) in scanTasks)
        {
            (IReadOnlyList<DiscoveredDevice> devices, int durationMs, string? error) = task.Result;
            string scannerName = scanner.Name;

            foreach (DiscoveredDevice device in devices)
            {
                if (!merged.TryGetValue(device.IpAddress, out MergedDevice? existing))
                {
                    existing = new MergedDevice
                    {
                        IpAddress = device.IpAddress,
                    };
                    merged[device.IpAddress] = existing;
                }

                existing.Merge(device);
            }

            if (!statsAgg.TryGetValue(scannerName, out (int Devices, int TotalMs, string? FirstError) agg))
            {
                agg = (0, 0, null);
            }

            statsAgg[scannerName] = (agg.Devices + devices.Count, agg.TotalMs + durationMs, agg.FirstError ?? error);
        }

        LastScannerStats = statsAgg
            .OrderBy(kv => kv.Key)
            .Select(kv => new ScannerStat(kv.Key, kv.Value.Devices, kv.Value.TotalMs, kv.Value.FirstError))
            .ToList();

        List<Fact> facts = new();
        foreach (MergedDevice device in merged.Values)
        {
            string ip = device.IpAddress;

            if (device.MacAddress is { Length: > 0 } mac)
            {
                facts.Add(Fact.Create(FactPaths.DiscoveredMAC, [deviceId, ip], mac) with
                {
                    Source = FactSource.NetworkDiscovery,
                });
            }

            if (device.Hostname is { Length: > 0 } hostname)
            {
                facts.Add(Fact.Create(FactPaths.DiscoveredHostname, [deviceId, ip], hostname) with
                {
                    Source = FactSource.NetworkDiscovery,
                });
            }

            facts.Add(Fact.Create(FactPaths.DiscoveredSources, [deviceId, ip], string.Join(",", device.Sources)) with
            {
                Source = FactSource.NetworkDiscovery,
            });

            // Every scanner attribute maps to a typed fact path — there is no raw Attr[key] sink.
            // A parsed signal we can't type is a signal we shouldn't emit; PromoteAttributes is the
            // single, total place that decides the home for each key.
            PromoteAttributes(facts, deviceId, ip, device.Attributes);
        }

        return facts;
    }

    private static async Task<(IReadOnlyList<DiscoveredDevice> Devices, int DurationMs, string? Error)> RunScannerAsync(
        INetworkScanner scanner,
        NetworkScanTarget target,
        CancellationToken ct
    )
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            IReadOnlyList<DiscoveredDevice> devices = await scanner.ScanAsync(target, ct);
            return (devices, (int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            NetworkDiscoveryCollectorLog.ScannerFailed(Log, ex, scanner.Name);
            return ([], (int)sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    private static List<NetworkScanTarget> EnumerateLocalSubnets()
    {
        List<NetworkScanTarget> targets = new();

        try
        {
            foreach (NetworkInterface iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    // Skip link-local (169.254.x.x)
                    byte[] addrBytes = addr.Address.GetAddressBytes();
                    if (addrBytes[0] == 169 && addrBytes[1] == 254)
                    {
                        continue;
                    }

                    int prefix = GetPrefixLength(addr.IPv4Mask);
                    if (prefix < 8 || prefix > 30)
                    {
                        continue;
                    }

                    IPAddress subnetAddress = GetSubnetAddress(addr.Address, addr.IPv4Mask);

                    targets.Add(
                        new NetworkScanTarget
                        {
                            SubnetAddress = subnetAddress,
                            PrefixLength = prefix,
                            LocalAddress = addr.Address,
                            InterfaceName = iface.Name,
                        }
                    );
                }
            }
        }
        catch { }

        return targets;
    }

    private static IPAddress GetSubnetAddress(IPAddress ip, IPAddress mask)
    {
        byte[] ipBytes = ip.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] subnet = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            subnet[i] = (byte)(ipBytes[i] & maskBytes[i]);
        }

        return new IPAddress(subnet);
    }

    private static int GetPrefixLength(IPAddress mask)
    {
        byte[] bytes = mask.GetAddressBytes();
        int count = 0;
        foreach (byte b in bytes)
        {
            byte v = b;
            while (v != 0)
            {
                count += v & 1;
                v >>= 1;
            }
        }

        return count;
    }

    // ── Attribute promotion helpers ───────────────────────────────────────────

    /// <summary>
    /// Promotes well-known scanner attributes (raw <c>Attr[key]</c> facts, which no
    /// projection consumes) to typed discovered-* fact paths so they are queryable.
    /// The Vendor/Model/Firmware lists are priority-ordered — the first present key wins.
    /// </summary>
    public static void PromoteAttributes(
        List<Fact> facts,
        string deviceId,
        string ip,
        Dictionary<string, string> attrs
    )
    {
        // ── Identity signals collapsed into the shared Vendor/Model/Firmware facts (first wins) ──
        // http.identity.* comes from generic HTTP fingerprinting; it ranks LAST so device-specific
        // protocols (ONVIF/UPnP/Roku/…) win when present.
        EmitFirstNonNull(facts, deviceId, ip, FactPaths.DiscoveredVendor, attrs, "onvif.manufacturer", "upnp.manufacturer", "http.identity.vendor");
        EmitFirstNonNull(
            facts, deviceId, ip, FactPaths.DiscoveredModel, attrs,
            "onvif.model", "roku.model", "upnp.model", "ipp.model", "eureka.model", "airplay.model", "hue.model",
            "roku.model_number", // Roku's exact SKU; last-resort fallback if the friendly model name is absent.
            "http.identity.model"
        );
        EmitFirstNonNull(
            facts, deviceId, ip, FactPaths.DiscoveredFirmware, attrs,
            "onvif.firmware", "roku.version", "hue.version", "eureka.version", "airplay.version",
            "ipp.firmware", "http.identity.firmware"
        );
        EmitAttrAs(facts, deviceId, ip, attrs, "http.identity.type", FactPaths.DiscoveredDeviceType);
        EmitAttrAs(facts, deviceId, ip, attrs, "http.identity.os", FactPaths.DiscoveredOs);
        EmitAttrAs(facts, deviceId, ip, attrs, "http.identity.serial", FactPaths.DiscoveredHttpSerial);
        EmitAttrAs(facts, deviceId, ip, attrs, "snmp.printer_serial", FactPaths.DiscoveredSnmpSerial);
        EmitAttrAs(facts, deviceId, ip, attrs, "http.identity.name", FactPaths.DiscoveredFriendlyName);
        EmitAttrAs(facts, deviceId, ip, attrs, "http.identity.source", FactPaths.DiscoveredHttpIdentitySource);
        EmitAttrAsDouble(facts, deviceId, ip, attrs, "http.identity.confidence", FactPaths.DiscoveredHttpConfidence);

        // Printer status/consumables/hardware detail (HP LEDM / Epson EWS follow-ups).
        EmitAttrAs(facts, deviceId, ip, attrs, "printer.status", FactPaths.DiscoveredPrinterStatus);
        EmitAttrAs(facts, deviceId, ip, attrs, "printer.alerts", FactPaths.DiscoveredPrinterAlerts);
        EmitAttrAs(facts, deviceId, ip, attrs, "printer.consumables", FactPaths.DiscoveredPrinterConsumables);
        EmitAttrAs(facts, deviceId, ip, attrs, "printer.product_number", FactPaths.DiscoveredPrinterProductNumber);

        // ── Fingerprint / stable-id signals ──
        EmitAttrAs(facts, deviceId, ip, attrs, "onvif.serial", FactPaths.DiscoveredOnvifSerial);
        EmitAttrAs(facts, deviceId, ip, attrs, "roku.serial", FactPaths.DiscoveredRokuSerial);
        EmitAttrAs(facts, deviceId, ip, attrs, "ssh.host-key-fp", FactPaths.DiscoveredSshHostKey);
        EmitAttrAs(facts, deviceId, ip, attrs, "hue.bridge_id", FactPaths.DiscoveredHueBridgeId);
        EmitAttrAs(facts, deviceId, ip, attrs, "onvif.hardware_id", FactPaths.DiscoveredOnvifHardwareId);
        EmitSsdpUuid(facts, deviceId, ip, attrs);
        EmitWsdUuid(facts, deviceId, ip, attrs);

        // ── Hostname signal (SNMP sysName IS the device's name) ──
        EmitAttrAs(facts, deviceId, ip, attrs, "snmp.sysname", FactPaths.DiscoveredHostname);

        // ── Text protocol/banner signals (device-detail context) ──
        EmitAttrAs(facts, deviceId, ip, attrs, "http.title", FactPaths.DiscoveredHttpTitle);
        EmitAttrAs(facts, deviceId, ip, attrs, "http.server", FactPaths.DiscoveredHttpServer);
        EmitAttrAs(facts, deviceId, ip, attrs, "http.url", FactPaths.DiscoveredHttpUrl);
        EmitAttrAs(facts, deviceId, ip, attrs, "http.favicon.md5", FactPaths.DiscoveredFaviconMd5);
        EmitAttrAs(facts, deviceId, ip, attrs, "smb2.dialect", FactPaths.DiscoveredSmb2Dialect);
        EmitAttrAs(facts, deviceId, ip, attrs, "ssh.banner", FactPaths.DiscoveredSshBanner);
        EmitAttrAs(facts, deviceId, ip, attrs, "ssdp.server", FactPaths.DiscoveredSsdpServer);
        EmitAttrAs(facts, deviceId, ip, attrs, "ssdp.st", FactPaths.DiscoveredSsdpSt);
        EmitAttrAs(facts, deviceId, ip, attrs, "upnp.device_type", FactPaths.DiscoveredUpnpDeviceType);
        EmitAttrAs(facts, deviceId, ip, attrs, "upnp.presentation_url", FactPaths.DiscoveredPresentationUrl);
        EmitAttrAs(facts, deviceId, ip, attrs, "rtsp.server", FactPaths.DiscoveredRtspServer);
        EmitAttrAs(facts, deviceId, ip, attrs, "rtsp.content_type", FactPaths.DiscoveredRtspContentType);
        EmitAttrAs(facts, deviceId, ip, attrs, "rtsp.methods", FactPaths.DiscoveredRtspMethods);
        EmitAttrAs(facts, deviceId, ip, attrs, "ldap.naming_context", FactPaths.DiscoveredLdapNamingContext);
        EmitAttrAs(facts, deviceId, ip, attrs, "ldap.server_name", FactPaths.DiscoveredLdapServerName);
        EmitAttrAs(facts, deviceId, ip, attrs, "mqtt.return_code", FactPaths.DiscoveredMqttReturnCode);
        EmitAttrAs(facts, deviceId, ip, attrs, "hue.api_version", FactPaths.DiscoveredHueApiVersion);
        EmitAttrAs(facts, deviceId, ip, attrs, "eureka.cast_version", FactPaths.DiscoveredEurekaCastVersion);
        EmitAttrAs(facts, deviceId, ip, attrs, "eureka.ssid", FactPaths.DiscoveredEurekaSsid);
        EmitAttrAs(facts, deviceId, ip, attrs, "ipp.location", FactPaths.DiscoveredIppLocation);
        EmitAttrAs(facts, deviceId, ip, attrs, "airplay.features", FactPaths.DiscoveredAirplayFeatures);
        EmitAttrAs(facts, deviceId, ip, attrs, "airplay.plist_format", FactPaths.DiscoveredAirplayPlistFormat);
        EmitAttrAs(facts, deviceId, ip, attrs, "wsd.types", FactPaths.DiscoveredWsdTypes);
        EmitAttrAs(facts, deviceId, ip, attrs, "wsd.metadata_version", FactPaths.DiscoveredWsdMetadataVersion);

        // ── TLS certificate fields ──
        EmitAttrAs(facts, deviceId, ip, attrs, "tls.cn", FactPaths.DiscoveredTlsCn);
        EmitAttrAs(facts, deviceId, ip, attrs, "tls.subject", FactPaths.DiscoveredTlsSubject);
        EmitAttrAs(facts, deviceId, ip, attrs, "tls.issuer", FactPaths.DiscoveredTlsIssuer);
        EmitAttrAs(facts, deviceId, ip, attrs, "tls.serial", FactPaths.DiscoveredTlsSerial);
        EmitAttrAsTimestamp(facts, deviceId, ip, attrs, "tls.expires", FactPaths.DiscoveredTlsNotAfter);

        // ── Numeric signals ──
        EmitAttrAsLong(facts, deviceId, ip, attrs, "bacnet.instance", FactPaths.DiscoveredBacnetInstance);
        EmitAttrAsLong(facts, deviceId, ip, attrs, "bacnet.vendor_id", FactPaths.DiscoveredBacnetVendorId);
        EmitAttrAsLong(facts, deviceId, ip, attrs, "http.status", FactPaths.DiscoveredHttpStatus);
        EmitAttrAsLong(facts, deviceId, ip, attrs, "http.favicon.mmh3", FactPaths.DiscoveredFaviconMmh3);
        EmitAttrAsLong(facts, deviceId, ip, attrs, "rtsp.port", FactPaths.DiscoveredRtspPort);
        EmitAttrAsLong(facts, deviceId, ip, attrs, "mqtt.port", FactPaths.DiscoveredMqttPort);
        EmitAttrAsLong(facts, deviceId, ip, attrs, "modbus.port", FactPaths.DiscoveredModbusPort);
        EmitAttrAsLong(facts, deviceId, ip, attrs, "modbus.unit_id", FactPaths.DiscoveredModbusUnitId);

        // ── Boolean signals ──
        EmitAttrAsBool(facts, deviceId, ip, attrs, "mqtt.auth_required", FactPaths.DiscoveredMqttAuthRequired);
        EmitAttrAsBool(facts, deviceId, ip, attrs, "onvif.auth_required", FactPaths.DiscoveredOnvifAuthRequired);

        // ── NBNS response header (scalar per Discovered IP) ──
        EmitAttrAs(facts, deviceId, ip, attrs, "nbns.op_code", FactPaths.DiscoveredNbnsOpCode);
        EmitAttrAs(facts, deviceId, ip, attrs, "nbns.result_code", FactPaths.DiscoveredNbnsResultCode);
        EmitAttrAsBool(facts, deviceId, ip, attrs, "nbns.authoritative", FactPaths.DiscoveredNbnsAuthoritative);
        EmitAttrAsBool(facts, deviceId, ip, attrs, "nbns.truncated", FactPaths.DiscoveredNbnsTruncated);
        EmitAttrAsBool(facts, deviceId, ip, attrs, "nbns.broadcast", FactPaths.DiscoveredNbnsBroadcast);
        EmitAttrAsBool(facts, deviceId, ip, attrs, "nbns.recursion_desired", FactPaths.DiscoveredNbnsRecursionDesired);
        EmitAttrAsBool(
            facts,
            deviceId,
            ip,
            attrs,
            "nbns.recursion_available",
            FactPaths.DiscoveredNbnsRecursionAvailable
        );

        // ── Multi-valued signals → list sub-dimensions (one fact per item) ──
        EmitAttrList(facts, deviceId, ip, attrs, "mdns.services", FactPaths.DiscoveredServiceName);
        EmitAttrList(facts, deviceId, ip, attrs, "nbns.names", FactPaths.DiscoveredNbnsName);
        EmitNbnsNameDetails(facts, deviceId, ip, attrs);
        EmitAttrList(facts, deviceId, ip, attrs, "coap.resources", FactPaths.DiscoveredCoapResource);
        EmitAttrList(facts, deviceId, ip, attrs, "coap.types", FactPaths.DiscoveredCoapContentFormat);

        // NOTE: upnp.friendly_name is already captured as DiscoveredHostname (the scanner sets
        // DiscoveredDevice.Hostname), so it needs no separate promotion here.
    }

    /// <summary>
    /// Emits the per-NetBIOS-name sibling attributes (suffix, description, owner node type,
    /// flags) NbnsScanner packs into "nbns.name_details" -- one pipe-delimited record per name,
    /// keyed by the same "{Name}&lt;{Suffix}&gt;" string used as the NbnsName[] dimension key in
    /// "nbns.names", so every sibling fact lands on the same list item.
    /// </summary>
    private static void EmitNbnsNameDetails(List<Fact> facts, string deviceId, string ip, Dictionary<string, string> attrs)
    {
        if (!attrs.TryGetValue("nbns.name_details", out string? joined) || string.IsNullOrWhiteSpace(joined))
        {
            return;
        }

        foreach (string item in joined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = item.Split('|');
            if (fields.Length != 9 || !long.TryParse(fields[1], out long suffix))
            {
                continue; // malformed record -- skip rather than emit partial/misaligned facts
            }

            string key = fields[0];
            string[] keys = [deviceId, ip, key];
            facts.Add(Fact.Create(FactPaths.DiscoveredNbnsSuffix, keys, suffix) with { Source = FactSource.Nbns });
            facts.Add(Fact.Create(FactPaths.DiscoveredNbnsSuffixDescription, keys, fields[2]) with
            {
                Source = FactSource.Nbns,
            });
            facts.Add(Fact.Create(FactPaths.DiscoveredNbnsOwnerNodeType, keys, fields[3]) with
            {
                Source = FactSource.Nbns,
            });
            facts.Add(Fact.Create(FactPaths.DiscoveredNbnsIsGroup, keys, bool.Parse(fields[4])) with
            {
                Source = FactSource.Nbns,
            });
            facts.Add(Fact.Create(FactPaths.DiscoveredNbnsIsPermanent, keys, bool.Parse(fields[5])) with
            {
                Source = FactSource.Nbns,
            });
            facts.Add(Fact.Create(FactPaths.DiscoveredNbnsIsActive, keys, bool.Parse(fields[6])) with
            {
                Source = FactSource.Nbns,
            });
            facts.Add(Fact.Create(FactPaths.DiscoveredNbnsIsInConflict, keys, bool.Parse(fields[7])) with
            {
                Source = FactSource.Nbns,
            });
            facts.Add(Fact.Create(FactPaths.DiscoveredNbnsIsBeingDeregistered, keys, bool.Parse(fields[8])) with
            {
                Source = FactSource.Nbns,
            });
        }
    }

    // Every attribute key below is written by exactly one scanner (verified against each
    // scanner's source), so the fact's provenance can be looked up from the key alone —
    // no need to thread a FactSource parameter through every EmitAttrX call site.
    private static readonly Dictionary<string, FactSource> KeyToSource = new(StringComparer.Ordinal)
    {
        ["airplay.features"] = FactSource.AirPlay,
        ["airplay.model"] = FactSource.AirPlay,
        ["airplay.plist_format"] = FactSource.AirPlay,
        ["airplay.version"] = FactSource.AirPlay,

        ["bacnet.instance"] = FactSource.BacnetScanner,
        ["bacnet.vendor_id"] = FactSource.BacnetScanner,

        ["coap.resources"] = FactSource.Coap,
        ["coap.types"] = FactSource.Coap,

        ["eureka.cast_version"] = FactSource.Eureka,
        ["eureka.model"] = FactSource.Eureka,
        ["eureka.ssid"] = FactSource.Eureka,
        ["eureka.version"] = FactSource.Eureka,

        ["http.favicon.md5"] = FactSource.HttpBanner,
        ["http.favicon.mmh3"] = FactSource.HttpBanner,
        ["http.identity.confidence"] = FactSource.HttpBanner,
        ["http.identity.firmware"] = FactSource.HttpBanner,
        ["http.identity.model"] = FactSource.HttpBanner,
        ["http.identity.name"] = FactSource.HttpBanner,
        ["http.identity.os"] = FactSource.HttpBanner,
        ["http.identity.serial"] = FactSource.HttpBanner,
        ["http.identity.source"] = FactSource.HttpBanner,
        ["http.identity.type"] = FactSource.HttpBanner,
        ["http.identity.vendor"] = FactSource.HttpBanner,
        ["http.server"] = FactSource.HttpBanner,
        ["http.status"] = FactSource.HttpBanner,
        ["http.title"] = FactSource.HttpBanner,
        ["http.url"] = FactSource.HttpBanner,

        ["hue.api_version"] = FactSource.PhilipsHue,
        ["hue.bridge_id"] = FactSource.PhilipsHue,
        ["hue.model"] = FactSource.PhilipsHue,
        ["hue.version"] = FactSource.PhilipsHue,

        ["ipp.firmware"] = FactSource.Ipp,
        ["ipp.location"] = FactSource.Ipp,
        ["ipp.model"] = FactSource.Ipp,

        ["ldap.naming_context"] = FactSource.Ldap,
        ["ldap.server_name"] = FactSource.Ldap,

        ["mdns.services"] = FactSource.Mdns,

        ["modbus.port"] = FactSource.ModbusScanner,
        ["modbus.unit_id"] = FactSource.ModbusScanner,

        ["mqtt.auth_required"] = FactSource.Mqtt,
        ["mqtt.port"] = FactSource.Mqtt,
        ["mqtt.return_code"] = FactSource.Mqtt,

        ["nbns.authoritative"] = FactSource.Nbns,
        ["nbns.broadcast"] = FactSource.Nbns,
        ["nbns.name_details"] = FactSource.Nbns,
        ["nbns.names"] = FactSource.Nbns,
        ["nbns.op_code"] = FactSource.Nbns,
        ["nbns.recursion_available"] = FactSource.Nbns,
        ["nbns.recursion_desired"] = FactSource.Nbns,
        ["nbns.result_code"] = FactSource.Nbns,
        ["nbns.truncated"] = FactSource.Nbns,

        ["onvif.auth_required"] = FactSource.Onvif,
        ["onvif.firmware"] = FactSource.Onvif,
        ["onvif.hardware_id"] = FactSource.Onvif,
        ["onvif.manufacturer"] = FactSource.Onvif,
        ["onvif.model"] = FactSource.Onvif,
        ["onvif.serial"] = FactSource.Onvif,

        ["roku.model"] = FactSource.Roku,
        ["roku.model_number"] = FactSource.Roku,
        ["roku.serial"] = FactSource.Roku,
        ["roku.version"] = FactSource.Roku,

        ["rtsp.content_type"] = FactSource.Rtsp,
        ["rtsp.methods"] = FactSource.Rtsp,
        ["rtsp.port"] = FactSource.Rtsp,
        ["rtsp.server"] = FactSource.Rtsp,

        ["smb2.dialect"] = FactSource.Smb2,

        ["snmp.printer_serial"] = FactSource.SnmpPrinter,
        ["snmp.sysname"] = FactSource.SnmpBroadcast,

        ["ssdp.server"] = FactSource.Ssdp,
        ["ssdp.st"] = FactSource.Ssdp,
        ["ssdp.usn"] = FactSource.Ssdp,
        ["upnp.device_type"] = FactSource.Ssdp,
        ["upnp.manufacturer"] = FactSource.Ssdp,
        ["upnp.model"] = FactSource.Ssdp,
        ["upnp.presentation_url"] = FactSource.Ssdp,

        ["ssh.banner"] = FactSource.SshBanner,
        ["ssh.host-key-fp"] = FactSource.SshBanner,

        ["tls.cn"] = FactSource.TlsCert,
        ["tls.expires"] = FactSource.TlsCert,
        ["tls.issuer"] = FactSource.TlsCert,
        ["tls.serial"] = FactSource.TlsCert,
        ["tls.subject"] = FactSource.TlsCert,

        ["wsd.address"] = FactSource.WsDiscovery,
        ["wsd.metadata_version"] = FactSource.WsDiscovery,
        ["wsd.types"] = FactSource.WsDiscovery,
    };

    private static FactSource InferSource(string attrKey) =>
        KeyToSource.GetValueOrDefault(attrKey, FactSource.NetworkDiscovery);

    private static void EmitAttrAs(
        List<Fact> facts,
        string deviceId,
        string ip,
        Dictionary<string, string> attrs,
        string attrKey,
        string factPathTemplate
    )
    {
        if (attrs.TryGetValue(attrKey, out string? value) && !string.IsNullOrWhiteSpace(value))
        {
            facts.Add(Fact.Create(factPathTemplate, [deviceId, ip], value) with { Source = InferSource(attrKey) });
        }
    }

    private static void EmitAttrAsLong(
        List<Fact> facts,
        string deviceId,
        string ip,
        Dictionary<string, string> attrs,
        string attrKey,
        string factPathTemplate
    )
    {
        if (attrs.TryGetValue(attrKey, out string? value)
         && long.TryParse(value, out long parsed))
        {
            facts.Add(Fact.Create(factPathTemplate, [deviceId, ip], parsed) with { Source = InferSource(attrKey) });
        }
    }

    private static void EmitAttrAsDouble(
        List<Fact> facts,
        string deviceId,
        string ip,
        Dictionary<string, string> attrs,
        string attrKey,
        string factPathTemplate
    )
    {
        if (attrs.TryGetValue(attrKey, out string? value)
         && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            facts.Add(Fact.Create(factPathTemplate, [deviceId, ip], parsed) with { Source = InferSource(attrKey) });
        }
    }

    /// <summary>Emits a real timestamp fact from an ISO-8601 attribute value (round-trip / UTC).</summary>
    private static void EmitAttrAsTimestamp(
        List<Fact> facts,
        string deviceId,
        string ip,
        Dictionary<string, string> attrs,
        string attrKey,
        string factPathTemplate
    )
    {
        if (attrs.TryGetValue(attrKey, out string? value)
         && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed
            ))
        {
            facts.Add(Fact.Create(factPathTemplate, [deviceId, ip], parsed) with { Source = InferSource(attrKey) });
        }
    }

    private static void EmitAttrAsBool(
        List<Fact> facts,
        string deviceId,
        string ip,
        Dictionary<string, string> attrs,
        string attrKey,
        string factPathTemplate
    )
    {
        if (attrs.TryGetValue(attrKey, out string? value)
         && bool.TryParse(value, out bool parsed))
        {
            facts.Add(Fact.Create(factPathTemplate, [deviceId, ip], parsed) with { Source = InferSource(attrKey) });
        }
    }

    /// <summary>
    /// Splits a comma-joined attribute into one fact per item under a list sub-dimension
    /// (<paramref name="factPathTemplate" /> ends in <c>...[].Leaf</c>); the item is both the
    /// dimension key and the leaf value. Empty items are skipped.
    /// </summary>
    private static void EmitAttrList(
        List<Fact> facts,
        string deviceId,
        string ip,
        Dictionary<string, string> attrs,
        string attrKey,
        string factPathTemplate
    )
    {
        if (!attrs.TryGetValue(attrKey, out string? joined) || string.IsNullOrWhiteSpace(joined))
        {
            return;
        }

        FactSource source = InferSource(attrKey);
        foreach (string item in joined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            facts.Add(Fact.Create(factPathTemplate, [deviceId, ip, item], item) with { Source = source });
        }
    }

    private static void EmitFirstNonNull(
        List<Fact> facts,
        string deviceId,
        string ip,
        string factPathTemplate,
        Dictionary<string, string> attrs,
        params string[] candidateKeys
    )
    {
        foreach (string key in candidateKeys)
        {
            if (attrs.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                facts.Add(Fact.Create(factPathTemplate, [deviceId, ip], value) with { Source = InferSource(key) });
                return;
            }
        }
    }

    private static void EmitSsdpUuid(
        List<Fact> facts,
        string deviceId,
        string ip,
        Dictionary<string, string> attrs
    )
    {
        if (!attrs.TryGetValue("ssdp.usn", out string? usn) || string.IsNullOrWhiteSpace(usn))
        {
            return;
        }

        // USN format: "uuid:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx[::service-type]"
        if (!usn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string uuidPart = usn[5..];
        int colonColon = uuidPart.IndexOf("::", StringComparison.Ordinal);
        if (colonColon >= 0)
        {
            uuidPart = uuidPart[..colonColon];
        }

        uuidPart = uuidPart.Trim();
        if (!string.IsNullOrEmpty(uuidPart))
        {
            facts.Add(Fact.Create(FactPaths.DiscoveredSsdpUuid, [deviceId, ip], uuidPart) with
            {
                Source = FactSource.Ssdp,
            });
        }
    }

    private static void EmitWsdUuid(
        List<Fact> facts,
        string deviceId,
        string ip,
        Dictionary<string, string> attrs
    )
    {
        if (!attrs.TryGetValue("wsd.address", out string? address) || string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        // WSD endpoint address: "urn:uuid:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
        const string urnPrefix = "urn:uuid:";
        if (!address.StartsWith(urnPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string uuidPart = address[urnPrefix.Length..].Trim();
        if (!string.IsNullOrEmpty(uuidPart))
        {
            facts.Add(Fact.Create(FactPaths.DiscoveredWsdUuid, [deviceId, ip], uuidPart) with
            {
                Source = FactSource.WsDiscovery,
            });
        }
    }

    private sealed class MergedDevice
    {
        public string IpAddress { get; set; } = "";
        public string? MacAddress { get; set; }
        public string? Hostname { get; set; }
        public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Merge(DiscoveredDevice device)
        {
            Sources.Add(device.Source);

            if (MacAddress is null && device.MacAddress is { Length: > 0 } mac)
            {
                MacAddress = mac;
            }

            if (Hostname is null && device.Hostname is { Length: > 0 } hostname)
            {
                Hostname = hostname;
            }

            foreach (KeyValuePair<string, string> attr in device.Attributes)
            {
                Attributes.TryAdd(attr.Key, attr.Value);
            }
        }
    }
}

internal static partial class NetworkDiscoveryCollectorLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Scanner '{ScannerName}' failed.")]
    public static partial void ScannerFailed(ILogger logger, Exception ex, string scannerName);
}