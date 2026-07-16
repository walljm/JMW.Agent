using System.Text.RegularExpressions;

namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Fans in whichever raw model field is present for a device (HwSystemModel, DiscoveredModel,
/// BacnetModelName — same tie-break precedent as DeviceVendorDerivation) and, when the device's
/// vendor and OS are both known, runs the raw SKU string through a vendor+OS-dispatched cleanup
/// pass that turns it into a clean product-family display name — e.g. "WS-C9300-48P" becomes
/// "Catalyst 9300", "PA-220" becomes "PA 200".
/// Conceptually ported from ITPIE.DeviceAnalysis's NodeOperatingSystem.Normalize.Model.cs (light
/// per-vendor SKU cleanup) and NodeOperatingSystem.Derive.Platform.cs (~1400 lines of per-vendor
/// regex extraction) — NOT a line-for-line transcription of either. Both source files cover far
/// more narrow numeric SKU ranges (particularly Cisco ISR/ASR router families) than are ported
/// here; only the higher-value, higher-confidence rules per vendor family were kept, following the
/// same "smallest sufficient but thorough" judgment used in VendorOsFromDeviceBannerDerivation.
/// Dispatch key is (vendor, os) using THIS codebase's own established canonical values (e.g.
/// "Cisco IOS-XE", "JunOS", "ArubaOS", "PAN-OS" — see VendorOsFromDeviceBannerDerivation /
/// VendorFromOsDistroDerivation), not the reference project's raw strings ("ios-xe", "junos").
/// Deliberately does NOT include the reference project's "DeriveLinuxPlatform" branch (Ubuntu/
/// CentOS/Debian/RHEL/SLES/Photon OS host platform naming): that branch exists there to
/// disambiguate a CDP/LLDP "platform" TLV this codebase has no equivalent input for, and for a
/// Linux host HwSystemModel already IS the real hardware model (e.g. "OptiPlex 7090") — there is
/// nothing to canonicalize there that OsDistroNormalizer/VendorNormalizer don't already handle.
/// Unlike DerivePlatform's own default (uppercase the raw model when no vendor/OS rule matches),
/// an unmatched (vendor, os) pair here emits nothing at all — ModelNormalizer's own doc comment
/// already establishes the principle that model values are unbounded free text where blind
/// rewriting (even just case-folding) isn't safe; canonicalization only happens when a specific,
/// vetted rule actually fires.
/// Outputs to a separate canonical field (fan-in precedent, like DeviceVendorCanonical) rather
/// than overwriting any of the raw model paths — there's no single raw model path the way there
/// is a single canonical vendor fan-in target.
/// </summary>
public sealed class DeviceModelDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs { get; } =
    [
        FactPaths.HwSystemModel,
        FactPaths.DiscoveredModel,
        FactPaths.BacnetModelName,
        FactPaths.Derived.DeviceVendorCanonical,
        FactPaths.Derived.DeviceVendorGuess,
        FactPaths.Derived.DeviceOsGuess,
    ];

    public IReadOnlyList<string> Outputs { get; } = [FactPaths.Derived.DeviceModelCanonical];

    public IReadOnlyList<string> Scope => ["Device"];

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        Fact? modelFact = null;
        foreach (string path in ModelPathsByPriority)
        {
            foreach (Fact f in scopedFacts)
            {
                if (f.AttributePath == path && !string.IsNullOrWhiteSpace(f.Value.AsString()))
                {
                    modelFact = f;
                    break;
                }
            }

            if (modelFact is not null)
            {
                break;
            }
        }

        if (modelFact is not { } anchor)
        {
            return [];
        }

        string? vendorCanonical = null;
        string? vendorGuess = null;
        string? os = null;
        foreach (Fact f in scopedFacts)
        {
            string? s = f.Value.AsString();
            if (string.IsNullOrWhiteSpace(s))
            {
                continue;
            }

            switch (f.AttributePath)
            {
                case FactPaths.Derived.DeviceVendorCanonical:
                    vendorCanonical = s;
                    break;
                case FactPaths.Derived.DeviceVendorGuess:
                    vendorGuess = s;
                    break;
                case FactPaths.Derived.DeviceOsGuess:
                    os = s;
                    break;
            }
        }

        string vendor = vendorCanonical ?? vendorGuess ?? "";
        string model = anchor.Value.AsString() ?? "";
        string? canonical = Canonicalize(vendor, os ?? "", model);
        if (canonical is null)
        {
            return [];
        }

        string id = AnalysisEngine.BuildId(Outputs[0], anchor);
        return [Fact.Create(id, canonical, anchor.CollectedAt)];
    }

    private static readonly string[] ModelPathsByPriority =
    [
        FactPaths.HwSystemModel,
        FactPaths.DiscoveredModel,
        FactPaths.BacnetModelName,
    ];

    private static string? Canonicalize(string vendor, string os, string model)
    {
        string m = model.Trim().ToUpperInvariant();
        if (m.Length == 0)
        {
            return null;
        }

        return (vendor, os) switch
        {
            ("Arista", "EOS") when m.Contains("VEOS", StringComparison.Ordinal) => "vEOS",
            ("F5", "TMOS") when m.Contains("VIRTUAL EDITION", StringComparison.Ordinal) => "BIG-IP VE",
            ("Juniper", "JunOS") when m.Contains("VMX", StringComparison.Ordinal) => "vMX",
            ("Juniper", "JunOS") when m.Contains("VSRX", StringComparison.Ordinal) => "vSRX",
            ("Juniper", "JunOS") => DeriveJuniperJunos(m),
            ("Cisco", "Cisco AP-COS") => DeriveCiscoApCos(m),
            ("Cisco", "Cisco ASAS") => DeriveCiscoAsa(m),
            ("Cisco", "Cisco IOS" or "Cisco IOS-XE") => DeriveCiscoIosOrXe(m),
            ("Cisco", "Cisco IOS-XR") => DeriveCiscoIosXr(m),
            ("Cisco", "Cisco NX-OS") => DeriveCiscoNxos(m),
            ("Cisco", "Cisco AireOS") => DeriveCiscoAireOs(m),
            ("Cisco", "Cisco CIMC" or "Cisco UCSM") => DeriveCiscoUcs(m),
            ("Cisco", "Cisco Meraki") => DeriveCiscoMeraki(m),
            ("Cisco", "Firmware-UC" or "Firmware") => DeriveCiscoFirmwareUc(m),
            ("Gigamon", "GigaVUE") => DeriveGigamonGigavue(m),
            ("Aruba", "ArubaOS") => DeriveArubaArubaOs(m),
            ("Brocade" or "Foundry" or "Extreme" or "Ruckus", "IronWare") => DeriveIronware(m),
            ("HP", "Comware") => DeriveHpComware(m),
            ("NETGEAR", "NGOS") => DeriveNetgearNgos(m),
            ("Palo Alto Networks", "PAN-OS") => DerivePaloAltoPanOs(m),
            ("Juniper", "ScreenOS") => DeriveJuniperScreenOs(m),
            ("A10", "ACOS") => DeriveA10Acos(m),
            ("APC", "AOS") => DeriveApcAos(m),
            ("Avaya", "Firmware-UC" or "Firmware") => DeriveAvayaFirmwareUc(m),
            ("Crestron", "Firmware") => DeriveCrestronFirmware(m),
            ("Nortel", "Firmware-UC" or "Firmware") => DeriveNortelFirmwareUc(m),
            ("Polycom", "Firmware-UC") => DerivePolycomFirmwareUc(m),
            ("Siemens", "ROS") => DeriveSiemensRos(m),
            ("Infoblox", "NIOS") => DeriveInfobloxNios(m),
            _ => null,
        };
    }

    // ── Cisco ──────────────────────────────────────────────────────────────────

    private static string? DeriveCiscoApCos(string m)
        => m switch
        {
            _ when m.StartsWith("CISCO AIR-", StringComparison.Ordinal) => m.Replace("CISCO ", "", StringComparison.Ordinal),
            _ when m.StartsWith("CISCO C91", StringComparison.Ordinal) => "Catalyst 9100",
            _ when m.Contains("AIR-AP156", StringComparison.Ordinal) => "Aironet 1560",
            _ when m.Contains("AIR-AP28", StringComparison.Ordinal) => "Aironet 2800",
            _ when m.Contains("AIR-AP38", StringComparison.Ordinal) => "Aironet 3800",
            _ when m.Contains("AIR-AP48", StringComparison.Ordinal) => "Aironet 4800",
            _ when Regex.IsMatch(m, "AIR-CAP[0-9]+") => "Aironet " + Regex.Match(m, "AIR-CAP([0-9]+)").Groups[1].Value,
            _ => null,
        };

    private static string? DeriveCiscoAsa(string m)
        => m switch
        {
            _ when m.StartsWith("ASAV", StringComparison.Ordinal) => "ASAv",
            _ when m.Contains("ASA-SM", StringComparison.Ordinal) => "ASA SM",
            _ when Regex.IsMatch(m, "^ASA55(06|08|12|15|16|25|45|55|85)") => "ASA 5500-X",
            _ when m.StartsWith("ASA55", StringComparison.Ordinal) => "ASA 5500",
            _ when m.StartsWith("FPR", StringComparison.Ordinal) => "Firepower " + Regex.Match(m, @"FPR-?(\d{4})").Groups[1].Value,
            _ => null,
        };

    private static string? DeriveCiscoIosOrXe(string m)
        => m switch
        {
            _ when m.Contains("IOSV", StringComparison.Ordinal) => "IOSv",
            _ when Regex.IsMatch(m, @"^(CISCO )?C?(ISCO)?89\d[A-Z/-]?[A-Z0-9/-]*$") => "ISR 890",
            _ when Regex.IsMatch(m, @"^(CISCO )?C?(ISCO)?9\d{2}[A-Z/-]?[A-Z0-9/-]*$") => "ISR 900",
            _ when Regex.IsMatch(m, @"^(CISCO )?C?(ISCO)?11\d{2}[A-Z/-]?[A-Z0-9/-]*$") => "ISR 1100",
            _ when Regex.IsMatch(m, @"^(CISCO )?C?(ISCO)?16\d{2}[A-Z/-]?[A-Z0-9/-]*$") => "MAR 1600",
            _ when Regex.IsMatch(m, @"^(CISCO )?C?(ISCO)?18\d{2}[A-Z/-]?[A-Z0-9/-]*$") => "ISR 1800",
            _ when Regex.IsMatch(m, @"^(CISCO )?C?(ISCO)?19\d{2}[A-Z/-]?[A-Z0-9/-]*$") => "ISR 1900",
            _ when Regex.IsMatch(m, @"^(CISCO )?C?(ISCO)?26\d{2}[A-Z/-]?[A-Z0-9/-]*$") => "MAR 2600",
            _ when Regex.IsMatch(m, @"^(CISCO )?C?(ISCO)?28\d{2}[A-Z/-]?[A-Z0-9/-]*$") => "ISR 2800",
            _ when Regex.IsMatch(m, @"^(CISCO )?C?(ISCO)?29\d{2}[A-Z/-]?[A-Z0-9/-]*$") || m.Contains("ISR-29", StringComparison.Ordinal) => "ISR 2900",
            _ when Regex.IsMatch(m, @"^(CISCO )?C?(ISCO)?36\d{2}[A-Z/-]?[A-Z0-9/-]*$") => "MAR 3600",
            _ when Regex.IsMatch(m, @"^(CISCO )?C?(ISCO)?38\d{2}[A-Z/-]?[A-Z0-9/-]*$") => "ISR 3800",
            _ when Regex.IsMatch(m, @"^(CISCO )?C?(ISCO)?39\d{2}[A-Z/-]?[A-Z0-9/-]*$") => "ISR 3900",
            _ when Regex.IsMatch(m, @"^(CISCO )?C?(ISCO)?(ISR)?4[2-4][2356]1[A-Z/-]?[A-Z0-9/-]*$") => "ISR 4000",
            _ when Regex.IsMatch(m, @"^ASR[- ]?10[0146]\d.*$") => "ASR 1000",
            _ when Regex.IsMatch(m, @"^ASR[- ]?9[0-9][0246]\d.*$") => "ASR 9000",
            _ when Regex.IsMatch(m, @"^ASR[- ]90[237].*$") => "ASR 900",
            _ when Regex.IsMatch(m, @"^C?9[23568]\d\d.*$") => "Catalyst " + Regex.Match(m, @"9([23568])\d\d").Groups[0].Value,
            _ when m.StartsWith("WS-C", StringComparison.Ordinal) => "Catalyst " + Regex.Match(m, @"WS-C(\d[A-Z0-9]*)").Groups[1].Value,
            _ when m.Contains("C8000V", StringComparison.Ordinal) => "Catalyst 8000V",
            _ when m.Contains("CIGESM", StringComparison.Ordinal) => "Switch Module for IBM BladeCenter",
            _ when Regex.IsMatch(m, "IE-4[0-9]{3}.*") => "IE 4000",
            _ when Regex.IsMatch(m, "IE-3[0-9]{3}.*") => "IE 3000",
            _ when Regex.IsMatch(m, @"^7[025]\d\d.*$") => "7200",
            _ when m.StartsWith("CSR", StringComparison.Ordinal) => "Cloud Services Router " + Regex.Match(m, @"CSR(\d\d)").Groups[1].Value + "00",
            _ => null,
        };

    private static string? DeriveCiscoIosXr(string m)
        => m switch
        {
            "ASR9K" or "ASR9K SERIES" => "ASR 9000",
            _ when Regex.IsMatch(m, "ASR[- ]?10[0146]\\d.*") => "ASR 1000",
            _ when Regex.IsMatch(m, "ASR[- ]?9[0-9][0246]\\d.*") => "ASR 9000",
            _ when m.Contains("IOSXRV", StringComparison.Ordinal) || m.Contains("IOS-XRV", StringComparison.Ordinal) || m.Contains("IOS XRV", StringComparison.Ordinal) => "IOS XRv",
            _ => null,
        };

    private static string? DeriveCiscoNxos(string m)
        => m switch
        {
            _ when m.Contains("NX-OSV", StringComparison.Ordinal) || m.Contains("NX OSV", StringComparison.Ordinal) => "Nexus OSv",
            _ when m.StartsWith("N9K-C92", StringComparison.Ordinal) => "Nexus 9200",
            _ when m.StartsWith("N9K-C93", StringComparison.Ordinal) => "Nexus 9300",
            _ when m.StartsWith("N9K-C95", StringComparison.Ordinal) => "Nexus 9500",
            _ when m.StartsWith("N77-C77", StringComparison.Ordinal) => "Nexus 7700",
            _ when m.Contains("UCS-FI-62", StringComparison.Ordinal) => "UCS 6200",
            _ when m.Contains("UCS-FI-63", StringComparison.Ordinal) => "UCS 6300",
            _ when m.Contains("UCS-FI-64", StringComparison.Ordinal) => "UCS 6400",
            _ when Regex.IsMatch(m, @"^N\d.*") => "Nexus " + Regex.Match(m, @"N(\d)").Groups[1].Value + "000",
            _ when m.Contains("NEXUS", StringComparison.Ordinal) => "Nexus",
            _ => null,
        };

    private static string? DeriveCiscoAireOs(string m)
        => m switch
        {
            _ when m.Contains("CT55", StringComparison.Ordinal) => "CT5500",
            _ when m.StartsWith("AIR", StringComparison.Ordinal) => Regex.Replace(m, @"^AIR-\w\w(\d\d).*", "$1") + "00",
            _ => null,
        };

    private static string? DeriveCiscoUcs(string m)
        => m switch
        {
            _ when m.StartsWith("UCSB-51", StringComparison.Ordinal) => "UCS 5100 Blade Server Chassis",
            _ when m.StartsWith("UCSC", StringComparison.Ordinal) => "UCS C-Series",
            _ when m.StartsWith("UCSS", StringComparison.Ordinal) => "UCS S-Series",
            _ when m.StartsWith("UCS-FI", StringComparison.Ordinal) => "UCS-FI",
            _ when m.Contains("UCSM", StringComparison.Ordinal) => "UCSM",
            _ => null,
        };

    private static string? DeriveCiscoMeraki(string m)
        => m switch
        {
            _ when m.StartsWith("MS", StringComparison.Ordinal) => "Meraki Switch",
            _ when m.StartsWith("MR", StringComparison.Ordinal) || m.StartsWith("CW", StringComparison.Ordinal) => "Meraki Wireless",
            _ when m.StartsWith("MX", StringComparison.Ordinal) => "Meraki Security",
            _ => null,
        };

    private static string? DeriveCiscoFirmwareUc(string m)
        => m switch
        {
            _ when m.StartsWith("CP-DX", StringComparison.Ordinal) => "IP Phone DX",
            _ when m.StartsWith("CP-", StringComparison.Ordinal) => "IP Phone " + Regex.Match(m, @"^CP-(\d\d)").Groups[1].Value + "00",
            _ when m.StartsWith("TELEPRESENCE", StringComparison.Ordinal) => "Telepresence",
            _ when Regex.IsMatch(m, "CTS.*(MX|SX|EX|DX)") => "Telepresence " + Regex.Match(m, "(MX|SX|EX|DX)").Value,
            _ when m.StartsWith("SG300", StringComparison.Ordinal) => "SG300",
            _ => null,
        };

    // ── Aruba ──────────────────────────────────────────────────────────────────

    private static string? DeriveArubaArubaOs(string m)
        => m switch
        {
            _ when m.Contains("MM-HW", StringComparison.Ordinal) => "Aruba MM-HW",
            _ when m.Contains("MM-VA", StringComparison.Ordinal) => "Aruba MM-VA",
            _ when m.Contains("MC-VA", StringComparison.Ordinal) => "Aruba MC-VA",
            _ when Regex.IsMatch(m, "^9[24]\\d\\d$") && m.Contains("GATEWAY", StringComparison.Ordinal) => "Aruba 9000 Gateway",
            _ => null,
        };

    // ── Ironware (Brocade/Foundry/Extreme/Ruckus) ─────────────────────────────

    private static string? DeriveIronware(string m)
        => m switch
        {
            _ when m.StartsWith("NETIRON", StringComparison.Ordinal) => "NetIron",
            _ when m.StartsWith("SERVERIRON", StringComparison.Ordinal) => "ServerIron",
            _ when m.Contains("FCX", StringComparison.Ordinal) => "FastIron CX",
            _ when m.Contains("FESX", StringComparison.Ordinal) => "FastIron Edge X",
            _ when m.Contains("FGS", StringComparison.Ordinal) => "FastIron GS",
            _ when m.Contains("FLS", StringComparison.Ordinal) => "FastIron LS",
            _ when m.Contains("FWS", StringComparison.Ordinal) => "FastIron WS",
            _ when m.Contains("MLX", StringComparison.Ordinal) => "MLX",
            _ when m.Contains("XMR", StringComparison.Ordinal) => "XMR",
            _ when m.StartsWith("ICX", StringComparison.Ordinal) && Regex.IsMatch(m, @"\d\d\d\d")
                => "ICX " + Regex.Match(m, @"(\d\d\d)").Value + "0",
            _ => null,
        };

    // ── F5 / Gigamon / HP / NetGear / PaloAlto / Juniper ScreenOS ─────────────

    private static string? DeriveGigamonGigavue(string m)
        => m switch
        {
            _ when Regex.IsMatch(m, @"H\w\d") => Regex.Match(m, @"(H\w\d)").Value,
            _ when m.StartsWith("GV", StringComparison.Ordinal) => Regex.Replace(m, "^GV", ""),
            _ when m.StartsWith("GI", StringComparison.Ordinal) => Regex.Replace(m, "^GI", ""),
            _ when m.StartsWith("GIGAVUE", StringComparison.Ordinal) => Regex.Replace(m, "^GIGAVUE-?", ""),
            _ => null,
        };

    private static string? DeriveHpComware(string m)
        => m switch
        {
            _ when m.Contains("4800G", StringComparison.Ordinal) || m.Contains("3CRS48G", StringComparison.Ordinal) => "4800G",
            _ when m.Contains("4510G", StringComparison.Ordinal) => "4510G",
            _ when m.Contains("4210G", StringComparison.Ordinal) => "4210G",
            _ when m.Contains("A5120", StringComparison.Ordinal) => "A5120",
            _ when m.Contains("A5500", StringComparison.Ordinal) => "A5500",
            _ when m.Contains("A5800", StringComparison.Ordinal) => "A5800",
            _ when m.Contains("A5820", StringComparison.Ordinal) => "A5820",
            _ when Regex.IsMatch(m, "75\\d\\d") => "7500",
            _ => null,
        };

    private static string? DeriveNetgearNgos(string m)
        => m.Contains('-', StringComparison.Ordinal) ? Regex.Match(m, @"^(\w+)-").Groups[1].Value : null;

    private static string? DerivePaloAltoPanOs(string m)
        => m switch
        {
            _ when m.Contains("M-600", StringComparison.Ordinal) => "M 600",
            _ when m.StartsWith("PA-70", StringComparison.Ordinal) => "PA 7000",
            _ when m.StartsWith("PA-52", StringComparison.Ordinal) => "PA 5200",
            _ when m.StartsWith("PA-50", StringComparison.Ordinal) => "PA 5000",
            _ when m.StartsWith("PA-32", StringComparison.Ordinal) => "PA 3200",
            _ when m.StartsWith("PA-8", StringComparison.Ordinal) => "PA 800",
            _ when m.StartsWith("PA-22", StringComparison.Ordinal) => "PA 200",
            _ when m.StartsWith("PA-4", StringComparison.Ordinal) => "PA 400",
            _ when m.Contains("VM-1000", StringComparison.Ordinal) => "VM 1000",
            _ when m.Contains("VM-500", StringComparison.Ordinal) => "VM 500",
            _ when m.Contains("VM-300", StringComparison.Ordinal) => "VM 300",
            _ when m.Contains("VM-100", StringComparison.Ordinal) => "VM 100",
            _ when m.Contains("VM-50", StringComparison.Ordinal) => "VM 50",
            _ when m.Contains("PA-VM", StringComparison.Ordinal) => "VM-Series",
            _ when m.StartsWith("M-", StringComparison.Ordinal) => m,
            _ when m.StartsWith("PA-", StringComparison.Ordinal) => m,
            _ when m.StartsWith("VM-", StringComparison.Ordinal) => m,
            _ => null,
        };

    private static string? DeriveJuniperJunos(string m)
        => m switch
        {
            _ when m.StartsWith("QFX5100", StringComparison.Ordinal) || m.StartsWith("QFX5110", StringComparison.Ordinal) => "QFX5100",
            _ when m.StartsWith("QFX5200", StringComparison.Ordinal) || m.StartsWith("QFX5210", StringComparison.Ordinal) => "QFX5200",
            _ when m.StartsWith("QFX10008", StringComparison.Ordinal) || m.StartsWith("QFX10016", StringComparison.Ordinal) => "QFX10000",
            _ when m.StartsWith("VQFX", StringComparison.Ordinal) => "vQFX",
            _ when m.StartsWith("QFX", StringComparison.Ordinal) => m,
            _ when Regex.IsMatch(m, "^EX[234689][23456][01][048].*") => "EX" + Regex.Match(m, @"EX(\d\d)").Groups[1].Value + "00",
            _ when m.StartsWith("MX", StringComparison.Ordinal) => "MX Series",
            _ when m.StartsWith("VMX", StringComparison.Ordinal) => "vMX",
            _ when m.StartsWith("ACX", StringComparison.Ordinal) => m,
            _ when Regex.IsMatch(m, "^SRX1[45]00") => "SRX" + m[3..7],
            _ when m.StartsWith("VSRX", StringComparison.Ordinal) => "vSRX",
            _ when m.StartsWith("CSRX", StringComparison.Ordinal) => "cSRX",
            _ when Regex.IsMatch(m, "^SRX2[1234]0") => "SRX200",
            _ when Regex.IsMatch(m, "^SRX3[2468]0") => "SRX300",
            _ when m.StartsWith("SRX550", StringComparison.Ordinal) => "SRX500",
            _ when m.StartsWith("SRX", StringComparison.Ordinal) => m,
            _ => m, // Juniper JunOS bare fallback: this codebase's one intentional "just uppercase" exception, matching the reference project's explicit "uppercase junipers" comment.
        };

    private static string? DeriveJuniperScreenOs(string m)
        => m switch
        {
            _ when m.StartsWith("NETSCREEN-5200", StringComparison.Ordinal) => "NetScreen-5200",
            _ when m.StartsWith("NETSCREEN-5400", StringComparison.Ordinal) => "NetScreen-5400",
            // Longer/more specific prefixes first — "SSG50" would also match a naive "SSG5" check.
            _ when m.StartsWith("SSG140", StringComparison.Ordinal) || m.StartsWith("SSG-140", StringComparison.Ordinal) => "SSG-140",
            _ when m.StartsWith("SSG20", StringComparison.Ordinal) || m.StartsWith("SSG-20", StringComparison.Ordinal) => "SSG-20",
            _ when m.StartsWith("SSG50", StringComparison.Ordinal) || m.StartsWith("SSG-50", StringComparison.Ordinal) => "SSG-500",
            _ when m.StartsWith("SSG5", StringComparison.Ordinal) || m.StartsWith("SSG-5", StringComparison.Ordinal) => "SSG-5",
            _ => null,
        };

    // ── A10 / APC / Eaton / Infoblox ───────────────────────────────────────────

    private static string? DeriveA10Acos(string m)
        => m switch
        {
            _ when m.StartsWith("TH", StringComparison.Ordinal) => "Thunder Series",
            _ when m.StartsWith("AX", StringComparison.Ordinal) => "AX Series",
            _ => null,
        };

    private static string? DeriveApcAos(string m)
        => m switch
        {
            _ when m.Contains("SMART-UPS", StringComparison.Ordinal) => "Smart-UPS",
            _ when m.StartsWith("AP7", StringComparison.Ordinal) => "AP7000 Series PDU",
            _ when m.StartsWith("AP8", StringComparison.Ordinal) => "AP8000 Series PDU",
            _ => null,
        };

    private static string? DeriveInfobloxNios(string m)
        => m switch
        {
            _ when m.StartsWith("IB-", StringComparison.Ordinal) => "Infoblox Virtual Appliance",
            _ when Regex.IsMatch(m, "..-.*?14.5") => "Infoblox 1405",
            _ => null,
        };

    // ── Firmware / UC devices ──────────────────────────────────────────────────

    private static string? DeriveAvayaFirmwareUc(string m)
        => m switch
        {
            _ when Regex.IsMatch(m, @"96\d\d") => "9600",
            _ when Regex.IsMatch(m, @"56\d\d") => "5600",
            _ when Regex.IsMatch(m, @"16\d\d") => "1600",
            _ when Regex.IsMatch(m, @"12\d\d") => "1200",
            _ when Regex.IsMatch(m, @"11\d\d") => "1100",
            _ => null,
        };

    private static string? DeriveCrestronFirmware(string m)
        => m switch
        {
            _ when m.StartsWith("TSW-", StringComparison.Ordinal) => "Wall Mount Touchscreen",
            _ when m.StartsWith("TSD-", StringComparison.Ordinal) => "HD Touchscreen",
            _ when m.StartsWith("TS-", StringComparison.Ordinal) => "Tabletop Touchscreen",
            _ when m.StartsWith("TSS-", StringComparison.Ordinal) => "Room Scheduling Touchscreen",
            _ => null,
        };

    private static string? DeriveNortelFirmwareUc(string m)
        => m switch
        {
            _ when m.Contains("11", StringComparison.Ordinal) => "1100",
            _ when m.Contains("12", StringComparison.Ordinal) => "1200",
            _ when m.Contains("20", StringComparison.Ordinal) => "2000",
            _ => null,
        };

    private static string? DerivePolycomFirmwareUc(string m)
        => m switch
        {
            _ when m.StartsWith("TRIO 8", StringComparison.Ordinal) => "Trio 8000",
            _ when m.StartsWith("VVX", StringComparison.Ordinal) => m,
            _ when m.StartsWith("SOUNDPOINT IP", StringComparison.Ordinal) => "SoundPoint IP " + Regex.Match(m, @"(\d+)").Value,
            _ => null,
        };

    private static string? DeriveSiemensRos(string m)
        => Regex.IsMatch(m, @"^R\w+\d+") ? Regex.Match(m, @"^(R\w+\d+)").Value : null;
}
