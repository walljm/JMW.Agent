using System.Text.RegularExpressions;

namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Infers a device's vendor and/or OS from a curated, ordered cascade of signatures matched
/// against whichever free-text device self-description banner(s) are present for that device:
/// SNMP sysDescr, BACnet description, the raw SSH identification banner, the SSDP SERVER header,
/// and hardware component description. All are the same class of signal — a vendor's own baked-in
/// product/OS banner text — just captured by different collectors/protocols, so they're
/// concatenated into one blob and scanned once rather than run through five separate cascades.
/// Especially useful against SSH: many network appliances (routers, switches, UPS/PDU controllers)
/// identify themselves in command output or banner text using the same product strings this
/// cascade already recognizes from SNMP.
/// Replaces the former separate VendorFromSnmpSysDescrDerivation/OsFromSnmpSysDescrDerivation —
/// this supersedes both with a single, much larger, carefully-ordered cascade (ordering here
/// mirrors source precedence; several entries are marked "must stay ordered" and should not be
/// reshuffled) rather than maintaining two small overlapping lists.
/// Conceptually ported from ITPIE.DeviceAnalysis's DiscoveryProtocol.Derive.VendorOs.cs (a CDP/LLDP
/// neighbor-banner vendor/OS derivation) and the substring/regex catch-all halves of
/// NodeOperatingSystem.Normalize.cs's GetVendor/GetOperatingSystem — adapted to this codebase's
/// (vendor, os) pairing and its own established canonical vendor/OS naming (e.g. "Cisco IOS-XE"
/// not bare "IOS-XE", to match VendorFromOsDistroDerivation's existing convention; "Palo Alto
/// Networks" not "PaloAlto"). A few entries in the source were corrected rather than ported
/// verbatim where the source's own vendor attribution looked like a copy/paste error (noted
/// inline) — e.g. "poweredge" is a Dell product line, not HP as the source had it.
/// Deliberately excludes a handful of the source's signatures that are too short/generic to
/// substring-match safely against free text (bare "gos", bare "pdu", combined "hp"+"gb"+"module")
/// — same discipline as the derivations this replaces.
/// Outputs to the "guess" fields, not the raw self-reported paths — see
/// FactPaths.Derived.DeviceVendorGuess/DeviceOsGuess for why (last-write-wins projection risk).
/// </summary>
public sealed class VendorOsFromDeviceBannerDerivation : IDerivation
{
    public IReadOnlyList<string> Inputs { get; } =
    [
        FactPaths.SnmpSysDescr,
        FactPaths.BacnetDescription,
        FactPaths.DiscoveredSshBanner,
        FactPaths.DiscoveredSsdpServer,
        FactPaths.HwComponentDescription,
    ];

    public IReadOnlyList<string> Outputs { get; } =
        [FactPaths.Derived.DeviceVendorGuess, FactPaths.Derived.DeviceOsGuess];

    public IReadOnlyList<string> Scope => ["Device"];

    public IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts)
    {
        List<string> parts = [];
        Fact? anchor = null;

        foreach (Fact f in scopedFacts)
        {
            if (f.AttributePath is not (
                FactPaths.SnmpSysDescr or
                FactPaths.BacnetDescription or
                FactPaths.DiscoveredSshBanner or
                FactPaths.DiscoveredSsdpServer or
                FactPaths.HwComponentDescription))
            {
                continue;
            }

            string? s = f.Value.AsString();
            if (string.IsNullOrWhiteSpace(s))
            {
                continue;
            }

            parts.Add(s);
            anchor ??= f;
        }

        if (anchor is not { } anchorFact)
        {
            return [];
        }

        string combined = string.Join(",", parts).ToLowerInvariant();

        foreach ((Func<string, bool> matches, string? vendor, string? os) in KnownSignatures)
        {
            if (!matches(combined))
            {
                continue;
            }

            List<Fact> results = [];
            if (vendor is not null)
            {
                results.Add(
                    Fact.Create(
                        AnalysisEngine.BuildId(FactPaths.Derived.DeviceVendorGuess, anchorFact),
                        vendor,
                        anchorFact.CollectedAt
                    )
                );
            }

            if (os is not null)
            {
                results.Add(
                    Fact.Create(
                        AnalysisEngine.BuildId(FactPaths.Derived.DeviceOsGuess, anchorFact),
                        os,
                        anchorFact.CollectedAt
                    )
                );
            }

            return results;
        }

        return [];
    }

    // Ordered — earlier entries take precedence over later ones that would otherwise also match
    // (e.g. specific Cisco IOS-XE/IOS-XR regexes before the generic "cisco ios" catch-all). The
    // combined blob is already lowercased before matching, so literals here are lowercase and
    // regexes don't need RegexOptions.IgnoreCase (a few carry a redundant (?i) from the source;
    // kept harmless rather than stripped, to stay close to the original for easier future diffing).
    private static readonly (Func<string, bool> Matches, string? Vendor, string? Os)[] KnownSignatures =
    [
        (s => Regex.IsMatch(s, @"^(([0-9\.]{1,4}){2,4}[0-9\.]{1,4}\s)?fw_version:afw_([0-9\.]{1,4}){2,4}[0-9\.]{1,3}"), "Broadcom", "Firmware"),
        (s => Regex.IsMatch(s, @"^sip[0-9x]{2,4}(_[0-9]{2,})?\.([0-9][\-0-9a-z]{1,15})(.loads)?,?(cisco ip phone)?"), "Cisco", "Firmware-UC"),
        (s => Regex.IsMatch(s, @"^updater:\s?(?:[\d\.]{1,3}){1,3}\d{4,5},\s?app:\s?(?:[\d\.]{1,3}){1,3}\d{4,5}"), "Polycom", "Firmware-UC"),
        (s => Regex.IsMatch(s, @"^cisco ios software,\s(isr|ies|c?(ie)?[0-9]{4}[a-z]{0,})\s"), "Cisco", "Cisco IOS"),
        (s => s.StartsWith("cisco internetwork operating system softwareios ", StringComparison.Ordinal), "Cisco", "Cisco IOS"),
        (s => Regex.IsMatch(s, @"cisco\s+systems.*cisco\s+controller.*version.*air"), "Cisco", "Cisco AireOS"),
        (s => Regex.IsMatch(s, @"^cisco ios software, c7600s[0-9a-z]+_?[0-9a-z]+"), "Cisco", "Cisco IOS"),
        (s => Regex.IsMatch(s, @"^sip\s([0-9][\-0-9a-z]{1,15}),?(cisco ip phone)"), "Cisco", "Firmware-UC"),
        (s => Regex.IsMatch(s, @"^sipdx\d\d"), "Cisco", "Firmware-UC"),
        (s => Regex.IsMatch(s, @"^cisco ap software, ap(?!(1g[45678])|(3g3))"), "Cisco", "Cisco IOS-XE"),
        (s => Regex.IsMatch(s, @"^cisco ios software, s[0-9a-z]+_?[0-9a-z]+"), "Cisco", "Cisco IOS"),
        (s => s.StartsWith("cisco ios software, catalyst l3 switch ", StringComparison.Ordinal), "Cisco", "Cisco IOS"),
        (s => Regex.IsMatch(s, @"^(?:\d{1,3}\.){2,}.*securestack\s[a-z\d]+"), "Extreme", "EnterasysOS"),
        (s => s.StartsWith("dell real time operating system software", StringComparison.Ordinal), "Dell", "Dell Networking OS"),
        (s => Regex.IsMatch(s, @"^cisco ios software,\s(ap|cbs)[0-9a-z]+\s"), "Cisco", "Cisco IOS"),
        (s => Regex.IsMatch(s, @"^(?:ce|ti|tc)(?:[\d\.]{1,3}){1,3}\d{1,2}"), "Cisco", "Firmware-UC"),
        (s => s.StartsWith("cisco application deployment engine os", StringComparison.Ordinal), "Cisco", "Cisco ADE-OS"),
        (s => Regex.IsMatch(s, @"^79xx_default_load.*(?:cisco ip phone)?"), "Cisco", "Firmware-UC"),
        (s => Regex.IsMatch(s, @"^(?:\d{1,3}\.){2,}.*cisco sg[0-9]{1,5}"), "Cisco", "Firmware"),
        (s => Regex.IsMatch(s, @"^(?:\d{1,3}\.){2,}.*m4100-[a-z\d]{2,}"), "NETGEAR", "NGOS"),
        (s => Regex.IsMatch(s, @"^itsv-1\s?(?:[\d\.]{1,3}){1,3}\d{2,5}"), "Zenitel", "Firmware-UC"),
        (s => Regex.IsMatch(s, @"^cti\sv(?:[\d]{1,3}\.?){1,3}\.\d{1,5}"), "Connect Tech", "Firmware"),
        (s => s.StartsWith("cisco ios software, ios-xe software", StringComparison.Ordinal), "Cisco", "Cisco IOS-XE"),
        (s => Regex.IsMatch(s, @"^apps\d\dsccp.*(?:cisco ip phone)?"), "Cisco", "Firmware-UC"),
        (s => Regex.IsMatch(s, @"^term\d\d\.default"), "Cisco", "Firmware-UC"),
        (s => Regex.IsMatch(s, @"^topology\/pod-[0-9]+\/node-[0-9]+"), "Cisco", "Cisco NX-OS"),
        (s => Regex.IsMatch(s, @"^aruba jl[6-7]\d{2}a+ +[fg]l[\d\.]+"), "Aruba", "AOS-CX"),
        (s => s.StartsWith("ethernet routing switch ", StringComparison.Ordinal), "Nortel", "NNCLI"),
        (s => s.StartsWith("cisco identity services engine", StringComparison.Ordinal), "Cisco", "Cisco ISE"),
        (s => Regex.IsMatch(s, @"^cisco ap software, ap1g[45678]"), "Cisco", "Cisco AP-COS"),
        (s => s.StartsWith("dell equallogic storage array", StringComparison.Ordinal), "Dell", "EqualLogic"),
        (s => s.StartsWith("cisco nexus operating system", StringComparison.Ordinal), "Cisco", "Cisco NX-OS"),
        (s => Regex.IsMatch(s, @"^linux.*ccm:\d{1,2}\.\d{1,2}"), "Cisco", "Cisco UCOS"),
        (s => Regex.IsMatch(s, @"^cisco ios software \[\w*\],"), "Cisco", "Cisco IOS-XE"),
        (s => Regex.IsMatch(s, @"^[a-z]+.*-\sip surveillance"), "Bosch", "Firmware"),
        (s => s.StartsWith("ibm machine type and model", StringComparison.Ordinal), "IBM", null),
        (s => s.StartsWith("cisco ios software, asr10", StringComparison.Ordinal), "Cisco", "Cisco IOS-XE"),
        (s => s.StartsWith("cisco ap software, ap3g3", StringComparison.Ordinal), "Cisco", "Cisco AP-COS"),
        (s => Regex.IsMatch(s, @"^fortiswitch\S+ v[\d+\.]+"), "Fortinet", "FortiOS"),
        (s => s.StartsWith("dell emc networking os", StringComparison.Ordinal), "Dell", "Dell Networking OS"),
        (s => s.StartsWith("cisco ios xr software,", StringComparison.Ordinal), "Cisco", "Cisco IOS-XR"),
        (s => s.StartsWith("brocade ", StringComparison.Ordinal) && s.Contains(" ironware", StringComparison.Ordinal), "Brocade", "IronWare"),
        (s => Regex.IsMatch(s, @"^palo +alto +networks"), "Palo Alto Networks", "PAN-OS"),
        (s => s.StartsWith("cisco systems ade os", StringComparison.Ordinal), "Cisco", "Cisco ADE-OS"),
        (s => s.StartsWith("cisco ios software", StringComparison.Ordinal), "Cisco", "Cisco IOS"),
        (s => s.Contains("meraki", StringComparison.Ordinal), "Cisco", "Cisco Meraki"),
        (s => s.StartsWith("nortel ip telephone", StringComparison.Ordinal), "Nortel", "Firmware-UC"),
        (s => Regex.IsMatch(s, @"^aruba jl[0-5]\d{2}a"), "Aruba", "ArubaOS"),
        (s => Regex.IsMatch(s, @",55(1|2|3)0-(24|48)t"), "Nortel", "NNCLI"),
        (s => s.StartsWith("extreme", StringComparison.Ordinal) && s.Contains("ironware", StringComparison.Ordinal), "Extreme", "IronWare"),
        (s => s.StartsWith("avaya", StringComparison.Ordinal) && s.Contains("ip", StringComparison.Ordinal) && s.Contains("phone", StringComparison.Ordinal), "Avaya", "Firmware-UC"),
        (s => s.StartsWith("sarix", StringComparison.Ordinal), "Pelco", "Firmware"),
        (s => s.StartsWith("spectra", StringComparison.Ordinal), "Pelco", "Firmware"),
        (s => s.StartsWith("brightsign", StringComparison.Ordinal), "BrightSign", "Firmware"),
        (s => s.StartsWith("enterasys networks", StringComparison.Ordinal), "Extreme", "EnterasysOS"),
        (s => Regex.IsMatch(s, @"^m4100-[a-z\d]{2,}"), "NETGEAR", "NGOS"),
        (s => s.StartsWith("emulex oneconnect", StringComparison.Ordinal), "Broadcom", "Firmware"),
        (s => s.StartsWith("npoint", StringComparison.Ordinal) && s.Contains("netscout", StringComparison.Ordinal), "Netscout", "nPoint OS"),
        (s => s.StartsWith("wind river linux", StringComparison.Ordinal), null, "Wind River Linux"),
        (s => s.StartsWith("ruckus", StringComparison.Ordinal) && s.Contains("ironware", StringComparison.Ordinal), "Ruckus", "IronWare"),
        (s => s.StartsWith("aastra ip phone", StringComparison.Ordinal), "Aastra", "Firmware-UC"),
        (s => s.StartsWith("cisco ios", StringComparison.Ordinal) && s.Contains("vios", StringComparison.Ordinal), "Cisco", "Cisco IOS"),
        (s => s.StartsWith("cisco ap software", StringComparison.Ordinal), "Cisco", "Cisco IOS-XE"),
        (s => s.StartsWith("cisco ip phone", StringComparison.Ordinal), "Cisco", "Firmware-UC"),
        (s => s.Contains("netapp release", StringComparison.Ordinal), "NetApp", "ONTAP"),
        (s => s.Contains("netapp hci", StringComparison.Ordinal), "NetApp", "Firmware"),
        (s => s.StartsWith("model", StringComparison.Ordinal) && s.Contains("arubaos", StringComparison.Ordinal), "Aruba", "ArubaOS"),
        (s => s.StartsWith("thunder series", StringComparison.Ordinal), "A10", "ACOS"),
        (s => s.Contains("opengear", StringComparison.Ordinal) && s.Contains("linux", StringComparison.Ordinal), "OpenGear", "Firmware"),
        // deliberately out of sort order — don't move (matches source).
        (s => Regex.IsMatch(s, @"^(rsg9(0|1)\d\w)|(rs9\d\d)|(rst9\d\d\w)|rs900|rs8000|rsg"), "Siemens", "ROS"),
        (s => Regex.IsMatch(s, @"^rx(1536|1524|1501|1510|1511|1512|1400|5000)"), "Siemens", "ROS"),
        (s => Regex.IsMatch(s, @"^rst2\d2\d|rsg2\d\d\d"), "Siemens", "ROS"),
        // resume sorting by pattern length.
        (s => s.StartsWith("cen-swpoe-16", StringComparison.Ordinal), "Crestron", "Firmware"),
        (s => s.StartsWith("webns", StringComparison.Ordinal) && s.Contains("cisco", StringComparison.Ordinal), "Cisco", "Cisco WebNS"),
        (s => s.StartsWith("dell force10", StringComparison.Ordinal), "Dell", "FOS"),
        (s => s.StartsWith("hpe comware", StringComparison.Ordinal), "HP", "Comware"),
        (s => s.StartsWith("cisco nx-os", StringComparison.Ordinal), "Cisco", "Cisco NX-OS"),
        (s => s.StartsWith("3com switch", StringComparison.Ordinal), "HP", "Comware"),
        (s => Regex.IsMatch(s, @"^sccp\s*\d+\."), "Cisco", "Firmware-UC"),
        (s => s.StartsWith("p0030801", StringComparison.Ordinal), "Cisco", "Firmware-UC"),
        (s => Regex.IsMatch(s, @"^ata\d\d\d"), "Cisco", "Firmware-UC"),
        (s => s.Contains("vmware esx", StringComparison.Ordinal), "VMware", "ESXi"),
        (s => s.Contains("redline communications", StringComparison.Ordinal), "RedlineCommunications", "Firmware"),
        (s => Regex.IsMatch(s, @"^(polycom|poly;trio|poly;ccx)"), "Polycom", "Firmware-UC"),
        (s => s.StartsWith("illustra", StringComparison.Ordinal), "Illustra", "Firmware"),
        (s => s.StartsWith("juniper", StringComparison.Ordinal), "Juniper", "JunOS"),
        (s => s.StartsWith("arubaos", StringComparison.Ordinal), "Aruba", "ArubaOS"),
        (s => s.StartsWith("arista", StringComparison.Ordinal), "Arista", "EOS"),
        (s => s.StartsWith("avaya", StringComparison.Ordinal), "Avaya", "Firmware-UC"),
        (s => Regex.IsMatch(s, @"^snc-wr\d"), "Sony", "Firmware"),
        (s => s.StartsWith("hp vc", StringComparison.Ordinal), "HP", "Comware"),
        (s => s.StartsWith("versa", StringComparison.Ordinal), "VersaNetworks", "Firmware"),
        (s => s.StartsWith("mojo", StringComparison.Ordinal), "Arista", "BusyBox"),
        (s => s.StartsWith("crestron", StringComparison.Ordinal), "Crestron", "Firmware"),
        (s => Regex.IsMatch(s, @"^(xnb-|xno-|xnd-|xnp-|xnv-|xnf-|xnz-)[\w\d]+ \d"), "HanwhaVision", "Firmware"),
        (s => Regex.IsMatch(s, @"^(ace-|aco-|acv-|ane-|ano-|anv-)[\w\d]+ \d"), "HanwhaVision", "Firmware"),
        (s => Regex.IsMatch(s, @"^(hcb-|hcd-|hcf-|hco-|hcp-|hcv-)[\w\d]+ \d"), "HanwhaVision", "Firmware"),
        (s => Regex.IsMatch(s, @"^(tnb-|tnm-|tno-|tnp-|tnu-|tnv-)[\w\d]+ \d"), "HanwhaVision", "Firmware"),
        (s => Regex.IsMatch(s, @"^(pnb-|pnd-|pnm-|pno-|pnv-)[\w\d]+ \d"), "HanwhaVision", "Firmware"),
        (s => Regex.IsMatch(s, @"^(qnd-|qne-|qno-|qnp-|qnv-)[\w\d]+ \d"), "HanwhaVision", "Firmware"),
        (s => Regex.IsMatch(s, @"^(scd-|sco-|scv-|snp-|scb-)[\w\d]+ \d"), "HanwhaVision", "Firmware"),
        (s => Regex.IsMatch(s, @"^(lnd-|lno-|lnv-)[\w\d]+ \d"), "HanwhaVision", "Firmware"),
        (s => s.StartsWith("hwt", StringComparison.Ordinal) && s.Contains("camera", StringComparison.Ordinal), "HanwhaVision", "Firmware"),
        (s => Regex.IsMatch(s, @"^(xw|xm)\.v\d[\w\d\.]+"), "HanwhaVision", "Firmware"),
        (s => s.StartsWith("dell emc", StringComparison.Ordinal), "Dell", "EMC-OS"),
        (s => s.Contains("dell", StringComparison.Ordinal) && s.Contains("thinos", StringComparison.Ordinal), "Dell", "ThinOS"),
        (s => Regex.IsMatch(s, @"prosafe.*switch|26-port gbe web smart+"), "NETGEAR", "NGOS"),
        (s => s.StartsWith("hp comware", StringComparison.Ordinal), "HP", "Comware"),
        (s => s.Contains("proliant", StringComparison.Ordinal), "HP", null),
        // Source attributed "poweredge" to HP; PowerEdge is a Dell server line, corrected here.
        (s => s.Contains("poweredge", StringComparison.Ordinal), "Dell", null),
        (s => Regex.IsMatch(s, @"^hp j\d\d\d\d.*switch"), "HP", "Comware"),
        (s => s.StartsWith("amn-1000", StringComparison.Ordinal), "Accedian Networks", "Firmware"),
        (s => s.Contains("ciena", StringComparison.Ordinal) && s.Contains("switch", StringComparison.Ordinal), "Ciena", "SAOS"),
        (s => s.Contains("packetwave platform", StringComparison.Ordinal), "Ciena", "SAOS"),
        (s => s.Contains("mikrotik", StringComparison.Ordinal) && s.Contains("routeros", StringComparison.Ordinal), "Mikrotik", "RouterOS"),
        // Source attributed "netvanta" to a "NetVanta" vendor; NetVanta is Adtran's product line.
        (s => s.Contains("netvanta", StringComparison.Ordinal), "Adtran", "AOS"),
        (s => s.Contains("nutanix", StringComparison.Ordinal), "Nutanix", "AOS"),
        (s => s.Contains("cradlepoint", StringComparison.Ordinal), "Cradlepoint", "NCOS"),
        (s => s.Contains("broadcom", StringComparison.Ordinal), "Broadcom", "Firmware"),
        (s => s.Contains("oce14", StringComparison.Ordinal), "Broadcom", "Firmware"),
        (s => Regex.IsMatch(s, @"me12\d\d os software"), "Cisco", "Cisco IOS"),
        (s => Regex.IsMatch(s, @"^velop,,$"), "Linksys", "Firmware"),
        (s => s.StartsWith("ap-0650-60010-us", StringComparison.Ordinal), "Motorola", "Firmware"),
        (s => s.Contains("dell smartfabric os", StringComparison.Ordinal), "Dell", "Linux"),
        (s => s.Contains("sc4020", StringComparison.Ordinal), "Dell", null),
        (s => s.Contains("dell", StringComparison.Ordinal), "Dell", null),
        (s => Regex.IsMatch(s, @"releasebuild-\d{8}"), "VMware", "ESXi"),
        (s => s.StartsWith("shure", StringComparison.Ordinal), "Shure", "Firmware"),
        (s => Regex.IsMatch(s, @"(?i)(a10\s*networks)"), "A10", "ACOS"),
        (s => s.Contains("amazonlinux", StringComparison.Ordinal), "Amazon", "Linux"),
        (s => Regex.IsMatch(s, @"^model *number:\s*(.*?) *$"), "APC", "AOS"),
        (s => s.Contains("mac os", StringComparison.Ordinal), "Apple", "MacOS"),
        (s => Regex.IsMatch(s, @"^hardware +id *: *(.*?) *$"), "AudioCodes", "PSOS"),
        (s => Regex.IsMatch(s, @"^arubaos \(model: (\S+)\), version ((?!kb|wc)\S+)"), "Aruba", "ArubaOS"),
        (s => Regex.IsMatch(s, @" *system description *: *aruba *(\w+) *.*? switch"), "Aruba", "ArubaOS"),
        (s => Regex.IsMatch(s, @"^(arubaos-cx) version\s+:\s+(.*?)\s*$"), "Aruba", "AOS-CX"),
        (s => Regex.IsMatch(s, @"^\s*veos\s*"), "Arista", "EOS"),
        (s => Regex.IsMatch(s, @"^\s*arista (\S+) *$"), "Arista", "EOS"),
        (s => Regex.IsMatch(s, @" *(?:sensor|device) *version: *\[(\d+\.\d+\.\d+)\] *$"), "Arista", "BusyBox"),
        (s => Regex.IsMatch(s, @"(?i)(sysdescr\s*:\s*ers\-8\d\d\d)"), "Avaya", "NNCLI"),
        (s => Regex.IsMatch(s, @"(?i)(sysobjectid:\s*\.?1\.3\.6\.1\.4\.1\.45\.)"), "Avaya", "ERSstackable"),
        (s => s.Contains("proxysg", StringComparison.Ordinal), "BlueCoat", "SGOS"),
        (s => s.Contains("brocade", StringComparison.Ordinal), "Brocade", "IronWare"),
        (s => Regex.IsMatch(s, @"firmware name:\s*nos_v[0-9]"), "Brocade", "NOS"),
        (s => Regex.IsMatch(s, @"(?i)(1\.3\.6\.1\.4\.1\.14179\.|cisco controller)"), "Cisco", "Cisco AireOS"),
        (s => Regex.IsMatch(s, @"^\W*ws-c\d\d\d\d software, version nmpsw:\s+\d\.\d\(\d+\)\s*$"), "Cisco", "Cisco CAT OS"),
        (s => Regex.IsMatch(s, @"^ +product name: (ucs.*?) *$"), "Cisco", "Cisco CIMC"),
        (s => s.Contains("cisco application control software", StringComparison.Ordinal), "Cisco", "Cisco ACSW"),
        (s => Regex.IsMatch(s, @"[c]isco\s+[a]daptive"), "Cisco", "Cisco ASAS"),
        (s => Regex.IsMatch(s, @"ws-svc-fwm-\d|pix"), "Cisco", "Cisco FWSMS"),
        (s => Regex.IsMatch(s, @"^product name: *(.*?) *sw version: *([\d\.]+)"), "Cisco", "Cisco CSS-OS"),
        (s => Regex.IsMatch(s, @"^\s+product name: (cisco firepower .*)$"), "Cisco", "Cisco FXOS"),
        (s => Regex.IsMatch(s, @"cisco (?:ios software, )?(ios-xe)(?!\s+xr)"), "Cisco", "Cisco IOS-XE"),
        (s => Regex.IsMatch(s, @"cisco (ios|internetwork operating system)(?!.*(xr|\-xe))"), "Cisco", "Cisco IOS"),
        (s => Regex.IsMatch(s, @"(cisco\s+ironport|asyncos |^\W*model:\s+c670\s*$)"), "Cisco", "Cisco IronPort"),
        (s => s.Contains("nx-os", StringComparison.Ordinal) || s.Contains("nexus operatin", StringComparison.Ordinal), "Cisco", "Cisco NX-OS"),
        (s => Regex.IsMatch(s, @"^\s+product name: (cisco ucs .*)$"), "Cisco", "Cisco UCSM"),
        (s => Regex.IsMatch(s, @"^os *version *: *ucos *(.*?) *$"), "Cisco", "Cisco UCOS"),
        (s => Regex.IsMatch(s, @"cisco\s+ios\s+xr"), "Cisco", "Cisco IOS-XR"),
        (s => s.Contains("xrv9k", StringComparison.Ordinal), "Cisco", "Cisco IOS-XR"),
        (s => s.Contains("cisco", StringComparison.Ordinal) && s.Contains("csr", StringComparison.Ordinal), "Cisco", "Cisco IOS-XE"),
        (s => s.Contains("softnas cloud express", StringComparison.Ordinal), "Buurst", "SoftNAS"),
        (s => s.StartsWith("axis", StringComparison.Ordinal), "AxisCommunications", "Firmware"),
        (s => s.Contains("debian", StringComparison.Ordinal), "Debian", "Linux"),
        (s => Regex.IsMatch(s, @"system object id.*?1\.3\.6\.1\.4\.1\.674"), "Dell", "FTOS"),
        (s => s.Contains("dell real time", StringComparison.Ordinal), "Dell", "FTOS"),
        (s => s.Contains("enterasys", StringComparison.Ordinal), "Extreme", "EnterasysOS"),
        (s => Regex.IsMatch(s, @"(?i)extreme\s+[snc]\d"), "Extreme", "EnterasysOS"),
        (s => Regex.IsMatch(s, @"\s(00:11:88)|(00-11-88)|(00-01-f4)|(00:01:f4)|(00-1f-45)|(00:1f:45)|(20-b3-99)|(20:b3:99)"), "Extreme", "EnterasysOS"),
        (s => Regex.IsMatch(s, @"(?i)matrix\s[sncev]\d\splatinum"), "Extreme", "EnterasysOS"),
        (s => s.Contains("foundry", StringComparison.Ordinal), "Foundry", "IronWare"),
        (s => Regex.IsMatch(s, @"(?i)(^\W*product model:\s*fireeye)"), "FireEye", "FOS"),
        (s => Regex.IsMatch(s, @"^(secureos)\s+\S+\s+?(.*?)\s+?(.*?)\s+?(?:mon|tue|wed|thu|fri|sat|sun).*?$"), "Forcepoint", "SecureOS"),
        (s => Regex.IsMatch(s, @"(?i)(^\W*secureos )"), "Forcepoint", "SecureOS"),
        (s => Regex.IsMatch(s, @"\.el[56]\.f5\."), "F5", "TMOS"),
        (s => s.Contains("big-ip", StringComparison.Ordinal), "F5", "TMOS"),
        (s => Regex.IsMatch(s, @"(?i:gigavue-\w-series)"), "Gigamon", "GigaVUE"),
        (s => Regex.IsMatch(s, @"(?i:(^\s+\d+\s+-{7}-+>\s+\d+\s+$)|(\(\s*\d+\s*\)\s+-{7}-+>\s+\(\s*\d+\s*\)))"), "Gigamon", "GigaVUE"),
        (s => Regex.IsMatch(s, @"base mac addr\s+:\s*[0-9a-f]{6}-[0-9a-f]{6}"), "HP", "ProVision"),
        (s => Regex.IsMatch(s, @"hpe?\s+comware"), "HP", "Comware"),
        (s => s.Contains("hewlett-packard development company", StringComparison.Ordinal), "HP", "Comware"),
        (s => Regex.IsMatch(s, @"hp\scomware\splatform\ssoftware"), "HP", "Comware"),
        (s => Regex.IsMatch(s, @"comware\ssoftware"), "HP", "Comware"),
        (s => Regex.IsMatch(s, @"software version 3com os"), "HP", "Comware"),
        (s => Regex.IsMatch(s, @"h3c\s+comware"), "HP", "Comware"),
        (s => s.Contains("3com corporation", StringComparison.Ordinal), "HP", "Comware"),
        (s => s.Contains("3com switch", StringComparison.Ordinal), "HP", "Comware"),
        (s => Regex.IsMatch(s, @"^hardware +id"), "Infoblox", "NIOS"),
        (s => Regex.IsMatch(s, "os/2"), "IBM", "OS/2"),
        (s => s.Contains("pulse connect secure", StringComparison.Ordinal), "Ivanti", "Pulse Secure VPN"),
        (s => s.Contains("junos", StringComparison.Ordinal), "Juniper", "JunOS"),
        (s => Regex.IsMatch(s, @"(?i)(screenos)|(netscreen)|(^\W*product name:\s*ssg\-)"), "Juniper", "ScreenOS"),
        (s => Regex.IsMatch(s, @"^\W*model:\s*slc48"), "Lantronix", "LOS"),
        (s => Regex.IsMatch(s, @"(?i)windows \d"), "Microsoft", "Windows"),
        (s => s.Contains("ms-dos", StringComparison.Ordinal), "Microsoft", "MS-DOS"),
        (s => s.Contains("windows", StringComparison.Ordinal), "Microsoft", "Windows"),
        (s => Regex.IsMatch(s, @"intelligent edge (desktop )?managed switch"), "NETGEAR", "NGOS"),
        (s => s.Contains("novell netware", StringComparison.Ordinal), "Novell", "Netware"),
        // Source attributed this ERS-8xxx pattern to Oracle; ERS 8xxx is a Nortel/Avaya switch line.
        (s => Regex.IsMatch(s, @"(?i)(sysdescr\s*:\s*ers-8[68][01][0369]\s*\(\d)"), "Avaya", "ACLI"),
        (s => s.Contains("solaris", StringComparison.Ordinal), "Oracle", "Solaris"),
        (s => s.Contains("panos", StringComparison.Ordinal), "Palo Alto Networks", "PAN-OS"),
        (s => Regex.IsMatch(s, @"model: (pa|vm)\-\d{3,}"), "Palo Alto Networks", "PAN-OS"),
        (s => s.Contains("ruckus", StringComparison.Ordinal), "Ruckus", "IronWare"),
        (s => s.Contains("samba", StringComparison.Ordinal), "Samba", "Samba"),
        (s => Regex.IsMatch(s, @"(?i)(hp\s+\S*\s+nx\s+ips)"), "Trend Micro", "TPOS"),
        (s => Regex.IsMatch(s, @"(?i)(system\s*model\s*\(\s*\S+\.\S+\s*\)\s*=\s*hp\s*sms)"), "Trend Micro", "TPSMSOS"),
        (s => s.Contains("vmfs", StringComparison.Ordinal) || s.Contains("vmimages", StringComparison.Ordinal) || s.Contains("vmupgrade", StringComparison.Ordinal) || s.Contains("esxi", StringComparison.Ordinal), "VMware", "ESXi"),
        (s => s.Contains("freebsd", StringComparison.Ordinal), null, "FreeBSD"),
        (s => s.Contains("sco openserver", StringComparison.Ordinal), null, "OpenServer"),
        (s => s.Contains("centos", StringComparison.Ordinal), "CentOS", "Linux"),
        (s => s.Contains("coreos", StringComparison.Ordinal), "CoreOS", "Linux"),
        (s => s.Contains("ubuntu", StringComparison.Ordinal), "Canonical", "Linux"),
        (s => s.Contains("novell linux", StringComparison.Ordinal), "Novell", "Linux"),
        // Source mapped both SLES ("suse linux enterprise") and bare "suse linux"/"opensuse" to
        // "Open SUSE", which conflates SLES with openSUSE; corrected to plain "SUSE" here.
        (s => s.Contains("suse linux enterprise", StringComparison.Ordinal), "SUSE", "Linux"),
        (s => s.Contains("opensuse", StringComparison.Ordinal) || s.Contains("suse linux", StringComparison.Ordinal), "SUSE", "Linux"),
        (s => Regex.IsMatch(s, @"oracle linux|oracle \d"), "Oracle", "Linux"),
        (s => Regex.IsMatch(s, @"(?i)rocky.*\d"), "Rocky Linux", "Linux"),
        (s => Regex.IsMatch(s, @"fedora linux|red hat fedora|redhat|red hat enter|rhel"), "Red Hat", "Linux"),
        (s => s.Contains("vmware photon os", StringComparison.Ordinal), "VMware", "Linux"),
        (s => Regex.IsMatch(s, @"netgate pfsense .+"), "Netgate", "pfSense"), // source attributed vendor "BSD"; corrected to Netgate, pfSense's maker
        (s => s.Contains("telepresence", StringComparison.Ordinal), "Cisco", "Firmware"),
        // Legacy fallback signatures from the derivations this replaces — not in the ported source,
        // kept so nothing already-working regresses.
        (s => s.Contains("edgeos", StringComparison.Ordinal), "Ubiquiti", "EdgeOS"),
        (s => s.Contains("unifi os", StringComparison.Ordinal), "Ubiquiti", "UniFi OS"),
        (s => s.Contains("pan-os", StringComparison.Ordinal), "Palo Alto Networks", "PAN-OS"),
        (s => s.Contains("procurve", StringComparison.Ordinal), "HP", null),
        (s => s.Contains("fortios", StringComparison.Ordinal), "Fortinet", "FortiOS"),
        (s => s.Contains("routeros", StringComparison.Ordinal), "Mikrotik", "RouterOS"),
        // Must stay last: bare "linux" carries no vendor information at all.
        (s => s.Contains("linux", StringComparison.Ordinal), null, "Linux"),
    ];
}