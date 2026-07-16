namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Refines the coarse <see cref="FactPaths.DeviceKind" /> bucket a collector sets at
/// identification time (<see cref="DeviceKinds.Host" />, <see cref="DeviceKinds.NetworkDevice" />)
/// into a more specific value once vendor/OS/model/chassis signals are available. Conceptually
/// borrowed from the ITPIE.DeviceAnalysis "ManufacturerType" derivation pattern (a (vendor, OS,
/// model) dispatch producing a device-category enum) but sized to this codebase's actual device
/// catalog (home/SMB network + IoT) rather than the enterprise carrier-grade hardware catalog
/// that pattern was built against.
/// Writes directly to <see cref="FactPaths.DeviceKind" /> — the same path collectors write to —
/// rather than a separate "guess" field like <see cref="FactPaths.Derived.DeviceVendorGuess" />/
/// <see cref="FactPaths.Derived.DeviceOsGuess" />. This is a deliberate, discussed departure from
/// that convention: unlike vendor/OS guesses (inferred from a proxy signal with no ground truth
/// to fall back to), Kind already has a collector-authoritative coarse value every cycle, and this
/// derivation only ever narrows it, never invents a value from nothing. The tradeoff accepted:
/// because Derive() only fires when its input facts (vendor/model/chassis/sysDescr) are present in
/// the SAME batch as the coarse Kind fact, a cycle that re-reports the coarse bucket without also
/// re-reporting those signals will not re-refine it that cycle — for collectors that report vendor/
/// model/sysDescr together with Kind on every poll (SnmpCollector, BacnetCollector, ModbusCollector)
/// this isn't a risk in practice, but it is a real gap if a future collector ever decouples them.
/// Only ever narrows: if no refinement rule matches, or the refined value equals the current
/// value, no fact is emitted — the collector's own value stands unmodified.
/// </summary>
public sealed class DeviceKindDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs { get; } =
    [
        FactPaths.DeviceKind,
        FactPaths.Derived.DeviceVendorCanonical,
        FactPaths.Derived.DeviceVendorGuess,
        FactPaths.Derived.DeviceOsGuess,
        FactPaths.Derived.DeviceModelCanonical,
        FactPaths.HwChassisType,
        FactPaths.HwSystemModel,
        FactPaths.DiscoveredModel,
        FactPaths.SnmpSysDescr,
    ];

    public IReadOnlyList<string> Outputs { get; } = [FactPaths.DeviceKind];

    public IReadOnlyList<string> Scope => ["Device"];

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        Fact? kindFact = null;
        string? vendorCanonical = null;
        string? vendorGuess = null;
        string? os = null;
        string? modelCanonical = null;
        string? chassisType = null;
        string? rawModel = null;
        string? sysDescr = null;

        foreach (Fact f in scopedFacts)
        {
            string? s = f.Value.AsString();
            if (string.IsNullOrWhiteSpace(s))
            {
                continue;
            }

            switch (f.AttributePath)
            {
                case FactPaths.DeviceKind:
                    kindFact = f;
                    break;
                case FactPaths.Derived.DeviceVendorCanonical:
                    vendorCanonical = s;
                    break;
                case FactPaths.Derived.DeviceVendorGuess:
                    vendorGuess = s;
                    break;
                case FactPaths.Derived.DeviceOsGuess:
                    os = s;
                    break;
                case FactPaths.Derived.DeviceModelCanonical:
                    modelCanonical = s;
                    break;
                case FactPaths.HwChassisType:
                    chassisType = s;
                    break;
                case FactPaths.HwSystemModel:
                case FactPaths.DiscoveredModel:
                    rawModel ??= s;
                    break;
                case FactPaths.SnmpSysDescr:
                    sysDescr = s;
                    break;
            }
        }

        if (kindFact is not { } anchor)
        {
            return [];
        }

        string kind = anchor.Value.AsString() ?? "";
        string vendor = vendorCanonical ?? vendorGuess ?? "";
        // Prefer the cleaned product-family name (DeviceModelDerivation) over the raw SKU text —
        // more reliable to dispatch on, e.g. "Catalyst 9300" rather than "WS-C9300-48P".
        string model = modelCanonical ?? rawModel ?? "";
        string? refined = Refine(kind, vendor, os ?? "", chassisType, model, sysDescr ?? "");

        if (refined is null || string.Equals(refined, kind, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        string id = AnalysisEngine.BuildId(Outputs[0], anchor);
        return [Fact.Create(id, refined, anchor.CollectedAt)];
    }

    private static string? Refine(string kind, string vendor, string os, string? chassisType, string model, string sysDescr)
    {
        if (string.Equals(kind, DeviceKinds.Host, StringComparison.OrdinalIgnoreCase))
        {
            return chassisType is { Length: > 0 } ? ChassisTypeToKind(chassisType) : null;
        }

        if (string.Equals(kind, DeviceKinds.NetworkDevice, StringComparison.OrdinalIgnoreCase))
        {
            return RefineNetworkDevice(vendor, os, model, sysDescr);
        }

        return null;
    }

    // dmidecode already emits the DMTF SMBIOS chassis-type table's own English labels (Linux only
    // — see HardwareCollector.cs), so this is a lookup against a well-specified enum, not a guess.
    private static readonly HashSet<string> LaptopChassisTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Portable", "Laptop", "Notebook", "Sub Notebook", "Convertible", "Detachable",
    };

    private static readonly HashSet<string> DesktopChassisTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Desktop", "Low Profile Desktop", "Pizza Box", "Mini Tower", "Tower", "All In One",
        "Space-saving", "Sealed-case PC", "Mini PC", "Stick PC", "Docking Station",
    };

    private static readonly HashSet<string> ServerChassisTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Main Server Chassis", "Rack Mount Chassis", "Blade", "Blade Enclosure", "Multi-system",
        "CompactPCI", "AdvancedTCA", "Expansion Chassis", "SubChassis", "Bus Expansion Chassis",
        "RAID Chassis",
    };

    private static readonly HashSet<string> TabletChassisTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tablet",
    };

    private static string? ChassisTypeToKind(string chassisType)
    {
        string c = chassisType.Trim();
        if (LaptopChassisTypes.Contains(c))
        {
            return DeviceKinds.Laptop;
        }

        if (DesktopChassisTypes.Contains(c))
        {
            return DeviceKinds.Desktop;
        }

        if (ServerChassisTypes.Contains(c))
        {
            return DeviceKinds.Server;
        }

        if (TabletChassisTypes.Contains(c))
        {
            return DeviceKinds.Tablet;
        }

        return null;
    }

    // Exact-vendor signatures only — same discipline as VendorFromOsDistroDerivation/
    // VendorOsFromDeviceBannerDerivation: each of these vendors is exclusively (or overwhelmingly,
    // for this codebase's home/SMB device population) one device category, so an exact match on
    // the already-canonicalized vendor name is safe. Not a substring scan over free text.
    // Camera/intercom/microphone vendors added from VendorOsFromDeviceBannerDerivation's own
    // vendor list — each is a single-purpose brand in this codebase's signature set (Sony is
    // deliberately excluded: it's a multi-category vendor, unlike these).
    private static readonly Dictionary<string, string> VendorKindSignatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Synology"] = DeviceKinds.Nas,
        ["QNAP"] = DeviceKinds.Nas,
        ["APC"] = DeviceKinds.Ups,
        ["Mikrotik"] = DeviceKinds.Router, // RouterOS-only vendor (see VendorFromOsDistroDerivation)
        ["Brother"] = DeviceKinds.Printer,
        ["Epson"] = DeviceKinds.Printer,
        ["Lexmark"] = DeviceKinds.Printer,
        ["Xerox"] = DeviceKinds.Printer,
        ["HanwhaVision"] = DeviceKinds.Camera,
        ["AxisCommunications"] = DeviceKinds.Camera,
        ["Illustra"] = DeviceKinds.Camera,
        ["Pelco"] = DeviceKinds.Camera,
        ["Zenitel"] = DeviceKinds.Intercom,
        ["Shure"] = DeviceKinds.Microphone,
    };

    // (vendor, os) -> kind for cases where the OS-guess value alone is a confident, unambiguous
    // signal, independent of model text. Uses THIS codebase's own canonical vendor/OS values (see
    // VendorOsFromDeviceBannerDerivation / VendorFromOsDistroDerivation), not the reference
    // project's raw lowercased strings — a literal port would silently never fire.
    // Conceptually ported from ITPIE.DeviceAnalysis's DeriveManufacturerType top-level (vendor, os)
    // switch; the reference project's Linux-distro-as-"vendor" HOSTS block (centos/canonical/
    // debian/red hat => Host) is deliberately not ported — this codebase's OS-distro values never
    // appear in the Vendor field (see OsDistroNormalizer), and Host refinement already has a more
    // precise mechanism (SMBIOS chassis type, above).
    private static string? KindFromVendorOs(string vendor, string os, string model)
        => (vendor, os) switch
        {
            ("Aruba", "AOS-CX") => DeviceKinds.Switch,
            ("HP", "ProVision") => DeviceKinds.Switch,
            ("Cisco", "Cisco ISE") => DeviceKinds.ServerAppliance,
            ("Cisco", "Cisco ADE-OS") => DeviceKinds.ServerAppliance,
            ("Cisco", "Cisco IOS-XR") => DeviceKinds.Router,
            ("Cisco", "Cisco UCOS") => DeviceKinds.UcSessionController,
            ("Forcepoint", "SecureOS") => DeviceKinds.Firewall,
            ("Gigamon", "GigaVUE") => DeviceKinds.Tap,
            ("Nortel", "NNCLI") => DeviceKinds.Switch,
            ("Avaya", "NNCLI") => DeviceKinds.Switch,
            ("Infoblox", "NIOS") => DeviceKinds.Application,
            ("NetApp", "ONTAP") => DeviceKinds.ServerAppliance,
            ("Netscout", "nPoint OS") => DeviceKinds.Application,
            ("OpenGear", "Firmware") => DeviceKinds.TerminalServer,
            ("AudioCodes", "PSOS") => DeviceKinds.Sbc,
            ("VMware", "ESXi") => DeviceKinds.VmHypervisor,
            // "Firmware-UC" is only ever produced by phone/UC-endpoint signatures in this
            // codebase's banner cascade (Cisco/Avaya/Polycom/Aastra/Zenitel/Nortel IP phones) —
            // model text distinguishes the video-conferencing subset from plain desk phones.
            (_, "Firmware-UC")
                when model.Contains("Telepresence", StringComparison.OrdinalIgnoreCase)
                  || model.Contains("Trio", StringComparison.OrdinalIgnoreCase)
                => DeviceKinds.Vtc,
            (_, "Firmware-UC") => DeviceKinds.Phone,
            _ => null,
        };

    // Vendor-invented product-family names, safe to substring-match regardless of which vendor
    // reported them (a multi-category vendor like HP or Ubiquiti can't be dispatched on vendor
    // name alone) — same "vendor-exclusive product name" discipline as KnownSignatures. Matched
    // primarily against the cleaned model family name from DeviceModelDerivation (e.g. "Catalyst
    // 9300", "ASR 1000") rather than raw SKU text — that derivation already did the SKU-parsing
    // work, so classifying the clean family name into a kind bucket is simpler and more reliable
    // than re-deriving from scratch.
    private static readonly (string Signature, string Kind)[] ProductSignatures =
    [
        // Printers
        ("LaserJet", DeviceKinds.Printer),
        ("OfficeJet", DeviceKinds.Printer),
        ("DeskJet", DeviceKinds.Printer),
        ("JetDirect", DeviceKinds.Printer),

        // Ubiquiti
        ("UniFi AP", DeviceKinds.AccessPoint),
        ("UniFi Switch", DeviceKinds.Switch),
        ("EdgeRouter", DeviceKinds.Router),
        ("Dream Machine", DeviceKinds.Router),
        ("UniFi Gateway", DeviceKinds.Router),

        // Cisco model families (from DeviceModelDerivation's Cisco dispatch)
        ("Catalyst", DeviceKinds.Switch),
        ("Nexus", DeviceKinds.Switch),
        ("Aironet", DeviceKinds.AccessPoint),
        ("ASA ", DeviceKinds.Firewall),
        ("ASAv", DeviceKinds.Firewall),
        ("Firepower", DeviceKinds.Firewall),
        ("Meraki Switch", DeviceKinds.Switch),
        ("Meraki Wireless", DeviceKinds.AccessPoint),
        ("Meraki Security", DeviceKinds.Firewall),
        ("UCS-FI", DeviceKinds.FabricManager),
        ("UCSM", DeviceKinds.FabricManager),
        ("UCS ", DeviceKinds.BladeServer),
        ("IOSv", DeviceKinds.Switch),
        ("ISR ", DeviceKinds.Router),
        ("ASR ", DeviceKinds.Router),
        ("MAR ", DeviceKinds.Router),
        ("Cloud Services Router", DeviceKinds.Router),

        // Juniper model families
        ("QFX", DeviceKinds.Switch),
        ("vMX", DeviceKinds.Router),
        ("MX Series", DeviceKinds.Router),
        ("SRX", DeviceKinds.Firewall),
        ("vSRX", DeviceKinds.Firewall),
        ("cSRX", DeviceKinds.Firewall),
        ("NetScreen", DeviceKinds.Firewall),
        ("SSG-", DeviceKinds.Firewall),

        // F5 / Gigamon
        ("BIG-IP", DeviceKinds.LoadBalancer),
        ("GigaVUE", DeviceKinds.Tap),

        // HP Comware / Ironware (Brocade/Foundry/Extreme/Ruckus) switch families
        ("4800G", DeviceKinds.Switch),
        ("4510G", DeviceKinds.Switch),
        ("4210G", DeviceKinds.Switch),
        ("A5120", DeviceKinds.Switch),
        ("A5500", DeviceKinds.Switch),
        ("A5800", DeviceKinds.Switch),
        ("A5820", DeviceKinds.Switch),
        ("NetIron", DeviceKinds.Router),
        ("ServerIron", DeviceKinds.LoadBalancer),
        ("FastIron", DeviceKinds.Switch),
        ("ICX ", DeviceKinds.Switch),
        ("MLX", DeviceKinds.Router),
        ("XMR", DeviceKinds.Router),

        // A10 / APC
        ("Thunder Series", DeviceKinds.LoadBalancer),
        ("AX Series", DeviceKinds.LoadBalancer),
        ("Smart-UPS", DeviceKinds.Ups),
        ("AP7000 Series PDU", DeviceKinds.Pdu),
        ("AP8000 Series PDU", DeviceKinds.Pdu),
        ("ePDU", DeviceKinds.Pdu),

        // Palo Alto
        ("PA 200", DeviceKinds.Firewall),
        ("PA 400", DeviceKinds.Firewall),
        ("PA 800", DeviceKinds.Firewall),
        ("PA 3200", DeviceKinds.Firewall),
        ("PA 5000", DeviceKinds.Firewall),
        ("PA 5200", DeviceKinds.Firewall),
        ("PA 7000", DeviceKinds.Firewall),
        ("VM-Series", DeviceKinds.Firewall),

        // UC / video conferencing
        ("Telepresence", DeviceKinds.Vtc),
        ("Trio", DeviceKinds.Vtc),
        ("Wall Mount Touchscreen", DeviceKinds.Vtc),
        ("HD Touchscreen", DeviceKinds.Vtc),
        ("Tabletop Touchscreen", DeviceKinds.Vtc),
        ("Room Scheduling Touchscreen", DeviceKinds.Vtc),
        ("IP Phone", DeviceKinds.Phone),
        ("SoundPoint IP", DeviceKinds.Phone),
        ("VVX", DeviceKinds.Phone),
    ];

    private static string? RefineNetworkDevice(string vendor, string os, string model, string sysDescr)
    {
        string? byVendorOs = KindFromVendorOs(vendor, os, model);
        if (byVendorOs is not null)
        {
            return byVendorOs;
        }

        if (vendor.Length > 0 && VendorKindSignatures.TryGetValue(vendor, out string? vendorKind))
        {
            return vendorKind;
        }

        foreach ((string signature, string kind) in ProductSignatures)
        {
            if (model.Contains(signature, StringComparison.OrdinalIgnoreCase)
             || sysDescr.Contains(signature, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return null;
    }
}
