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
        string? chassisType = null;
        string? model = null;
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
                case FactPaths.HwChassisType:
                    chassisType = s;
                    break;
                case FactPaths.HwSystemModel:
                case FactPaths.DiscoveredModel:
                    model ??= s;
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
        string? refined = Refine(kind, vendor, chassisType, model ?? "", sysDescr ?? "");

        if (refined is null || string.Equals(refined, kind, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        string id = AnalysisEngine.BuildId(Outputs[0], anchor);
        return [Fact.Create(id, refined, anchor.CollectedAt)];
    }

    private static string? Refine(string kind, string vendor, string? chassisType, string model, string sysDescr)
    {
        if (string.Equals(kind, DeviceKinds.Host, StringComparison.OrdinalIgnoreCase))
        {
            return chassisType is { Length: > 0 } ? ChassisTypeToKind(chassisType) : null;
        }

        if (string.Equals(kind, DeviceKinds.NetworkDevice, StringComparison.OrdinalIgnoreCase))
        {
            return RefineNetworkDevice(vendor, model, sysDescr);
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

    // Exact-vendor signatures only — same discipline as VendorFromSnmpSysDescrDerivation/
    // VendorFromOsDistroDerivation: each of these vendors is exclusively (or overwhelmingly, for
    // this codebase's home/SMB device population) one device category, so an exact match on the
    // already-canonicalized vendor name is safe. Not a substring scan over free text.
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
    };

    // Vendor-invented product-family names, safe to substring-match regardless of which vendor
    // reported them (a multi-category vendor like HP or Ubiquiti can't be dispatched on vendor
    // name alone) — same "vendor-exclusive product name" discipline as
    // OsFromSnmpSysDescrDerivation's KnownSignatures.
    private static readonly (string Signature, string Kind)[] ProductSignatures =
    [
        ("LaserJet", DeviceKinds.Printer),
        ("OfficeJet", DeviceKinds.Printer),
        ("DeskJet", DeviceKinds.Printer),
        ("JetDirect", DeviceKinds.Printer),
        ("UniFi AP", DeviceKinds.AccessPoint),
        ("UniFi Switch", DeviceKinds.Switch),
        ("EdgeRouter", DeviceKinds.Router),
        ("Dream Machine", DeviceKinds.Router),
        ("UniFi Gateway", DeviceKinds.Router),
    ];

    private static string? RefineNetworkDevice(string vendor, string model, string sysDescr)
    {
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
