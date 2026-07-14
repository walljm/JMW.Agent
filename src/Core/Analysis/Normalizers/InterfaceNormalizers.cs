namespace JMW.Discovery.Core.Analysis.Normalizers;

/// <summary>
/// Normalizes interface speed to bits-per-second (long).
/// Handles the representations that appear across collection sources:
/// String with unit suffix (SSH CLI, NETCONF, display strings):
/// "1Gbps", "1G", "1000Mbps", "100Mbps", "10G", "100G", "1000BASE-T" → bps long
/// Long already in bps (Go MetricSnapshot after collector conversion):
/// 1_000_000_000 → pass through
/// Long in Mbps (defensive: some collectors forget to multiply):
/// Heuristic: ≤ 400_000 → likely Mbps (max real-world ~400Gbps = 400_000 Mbps)
/// </summary>
public sealed class SpeedNormalizer : INormalizer
{
    public IReadOnlyList<string> AttributePathPatterns => [FactPaths.InterfaceSpeedBps];

    public FactValue? Normalize(FactValue raw)
    {
        if (raw.AsString() is { } s)
        {
            return ParseSpeedString(s.Trim());
        }

        if (raw.AsLong() is { } v)
        {
            if (v <= 0)
            {
                return null;
            }

            return v <= 400_000
                ? FactValue.FromLong(v * 1_000_000) // likely Mbps — convert
                : FactValue.FromLong(v); // already bps
        }

        return null;
    }

    private static FactValue? ParseSpeedString(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        string lower = s.ToLowerInvariant();

        // Strip "BASE-x" suffix: "1000BASE-T" → "1000", "1000BASE-LX" → "1000"
        int baseIdx = lower.IndexOf("base-", StringComparison.Ordinal);
        if (baseIdx > 0)
        {
            lower = lower[..baseIdx].TrimEnd();
        }

        // Compact no-unit forms
        if (lower is "10g")
        {
            return FactValue.FromLong(10_000_000_000L);
        }

        if (lower is "25g")
        {
            return FactValue.FromLong(25_000_000_000L);
        }

        if (lower is "40g")
        {
            return FactValue.FromLong(40_000_000_000L);
        }

        if (lower is "100g")
        {
            return FactValue.FromLong(100_000_000_000L);
        }

        if (lower is "400g")
        {
            return FactValue.FromLong(400_000_000_000L);
        }

        int unitStart = 0;
        while (unitStart < lower.Length && (char.IsDigit(lower[unitStart]) || lower[unitStart] == '.'))
        {
            unitStart++;
        }

        if (unitStart == 0)
        {
            return null;
        }

        if (!double.TryParse(lower[..unitStart], out double num))
        {
            return null;
        }

        // Match the raw unit suffix — do NOT strip trailing 's' or '/'.
        // "gbps".TrimEnd('s') = "gbp" which matches nothing; just compare directly.
        string unit = lower[unitStart..].Trim();
        double multiplier = unit switch
        {
            "gbit/s" or "gbps" or "gbit" or "gb" or "g" => 1_000_000_000.0,
            "mbit/s" or "mbps" or "mbit" or "mb" or "m" => 1_000_000.0,
            "kbit/s" or "kbps" or "kbit" or "kb" or "k" => 1_000.0,
            "bit/s" or "bps" or "bit" or "b" or "" => 1.0,
            _ => -1.0,
        };

        if (multiplier < 0)
        {
            return null;
        }

        // Compute in double first, then range-check before casting.
        // (long)(num * multiplier) silently overflows to a negative value when
        // the input is pathologically large (e.g. "999999999Gbps").
        double bps = num * multiplier;
        if (bps <= 0 || bps > long.MaxValue)
        {
            return null;
        }

        return FactValue.FromLong((long)bps);
    }
}

/// <summary>
/// Normalizes interface names to their canonical SHORT form.
/// Covers Cisco IOS/IOS-XE/IOS-XR/NX-OS, Arista EOS, Huawei VRP,
/// HPE Comware, Brocade/Ruckus ICX, Dell OS10, Extreme XOS, and
/// Juniper JunOS (already canonical — passes through unchanged).
/// Only affects the DISPLAY VALUE (Device[].Interface[].Name).
/// The KEY in the fact ID must be canonicalized in the COLLECTOR before
/// the fact is created — use <see cref="Canonicalize" /> for that.
/// Platform ambiguity: "Management" maps to "Mg" (Cisco convention).
/// Arista uses "Ma" — operators targeting Arista should pre-canonicalize
/// in the collector using Canonicalize() and pass the short form as the key.
/// </summary>
public sealed class InterfaceNameNormalizer : INormalizer
{
    public IReadOnlyList<string> AttributePathPatterns => [FactPaths.InterfaceName];

    public FactValue? Normalize(FactValue raw) => Normalize(raw, default);

    // Context-aware implementation: vendor resolves ambiguous prefix mappings.
    public FactValue? Normalize(FactValue raw, NormalizationContext ctx)
    {
        string? name = raw.AsString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return FactValue.FromString(Canonicalize(name.Trim(), ctx));
    }

    /// <summary>
    /// Produces the canonical short form for a given interface name.
    /// Exposed as a static method so collectors can call it when building
    /// the fact ID key — pass the device's NormalizationContext for
    /// vendor-specific disambiguation.
    /// </summary>
    public static string Canonicalize(string name, NormalizationContext ctx = default)
    {
        // ── Vendor-specific overrides applied before the general prefix map ──
        //
        // "Management" → "Mg" (Cisco) vs "Ma" (Arista). Without context the
        // general map uses "Mg". Override when we know the device is Arista.
        if (ctx.VendorIs("arista"))
        {
            if (name.StartsWith("Management", StringComparison.OrdinalIgnoreCase))
            {
                return "Ma" + name[10..];
            }
            // Arista uses "Ethernet" (→ "Et"), already in the general map below.
        }

        // Juniper already uses canonical short forms — pass through immediately
        // to avoid any accidental prefix matches (ge-, xe-, et- are already short).
        if (ctx.VendorIs("juniper") || ctx.VendorIs("junos"))
        {
            return name;
        }

        foreach ((string longForm, string shortForm) in PrefixMap)
        {
            if (name.StartsWith(longForm, StringComparison.OrdinalIgnoreCase))
            {
                return shortForm + name[longForm.Length..];
            }
        }

        return name; // already short / unknown — pass through
    }

    // ── Prefix map ────────────────────────────────────────────────────────
    //
    // Rules:
    //   • Sorted longest-first so "TenGigabitEthernet" is checked before "GigabitEthernet"
    //   • Case-insensitive matching (OrdinalIgnoreCase in Canonicalize)
    //   • Platforms that already use short names (Juniper, Linux, Palo Alto,
    //     Fortinet, Nokia) need no entries — they pass through unchanged
    //   • Entries for the same concept from different platforms produce the
    //     SAME canonical short form (e.g. all 10G variants → "Te")

    private static readonly (string Long, string Short)[] PrefixMap =
    [
        // ── 800G ──────────────────────────────────────────────────────────
        ("EightHundredGigabitEthernet", "EiHu"), // future / 800G

        // ── 400G ──────────────────────────────────────────────────────────
        ("FourHundredGigabitEthernet", "FoHu"), // Cisco IOS-XE
        ("FourHundredGigE", "FoHu"), // Cisco IOS-XR
        ("400GE", "FoHu"), // Huawei

        // ── 200G ──────────────────────────────────────────────────────────
        ("TwoHundredGigabitEthernet", "TwoHu"), // Cisco IOS-XE
        ("TwoHundredGigE", "TwoHu"), // Cisco IOS-XR
        ("200GE", "TwoHu"), // Huawei

        // ── 100G ──────────────────────────────────────────────────────────
        ("HundredGigabitEthernet", "Hu"), // Cisco IOS-XE
        ("HundredGigE", "Hu"), // Cisco IOS-XR
        ("HundredGig-Ethernet", "Hu"), // Cisco NX-OS
        ("100GE", "Hu"), // Huawei / Dell OS10
        ("100-GigabitEthernet", "Hu"), // HPE Comware

        // ── 50G ───────────────────────────────────────────────────────────
        ("FiftyGigabitEthernet", "Fi"), // Cisco IOS-XE
        ("FiftyGigE", "Fi"), // Cisco IOS-XR
        ("50GE", "Fi"), // Huawei

        // ── 40G ───────────────────────────────────────────────────────────
        ("FortyGigabitEthernet", "Fo"), // Cisco IOS-XE
        ("FortyGigE", "Fo"), // Cisco IOS-XR
        ("FortyGig-Ethernet", "Fo"), // Cisco NX-OS
        ("40GE", "Fo"), // Huawei
        ("40-GigabitEthernet", "Fo"), // HPE Comware

        // ── 25G ───────────────────────────────────────────────────────────
        ("TwentyFiveGigabitEthernet", "Twe"), // Cisco IOS-XE
        ("TwentyFiveGigE", "Twe"), // Cisco IOS-XR
        ("TwentyFiveGig-Ethernet", "Twe"), // Cisco NX-OS
        ("25GE", "Twe"), // Huawei

        // ── 10G ───────────────────────────────────────────────────────────
        ("TenGigabitEthernet", "Te"), // Cisco IOS-XE / Huawei / Dell / HPE
        ("TenGigE", "Te"), // Cisco IOS-XR
        ("TenGig-Ethernet", "Te"), // Cisco NX-OS
        ("TenGig", "Te"), // Brocade ICX
        ("10GE", "Te"), // Huawei
        ("10GigabitEthernet", "Te"), // some HPE variants
        ("Ten-GigabitEthernet", "Te"), // HPE Comware
        ("tengigabitethernet", "Te"), // lowercase variants

        // ── 5G ────────────────────────────────────────────────────────────
        ("FiveGigabitEthernet", "Fi5"), // Cisco (multi-gig switches)
        ("FiveGigE", "Fi5"), // Cisco IOS-XR
        ("5GE", "Fi5"), // Huawei

        // ── 2.5G ──────────────────────────────────────────────────────────
        ("TwoPointFiveGigabitEthernet", "TwoF"), // Cisco (multi-gig)
        ("2.5GE", "TwoF"), // Huawei
        ("TwoGigabitEthernet", "Two"), // Cisco (2G)

        // ── 1G ────────────────────────────────────────────────────────────
        ("GigabitEthernet", "Gi"), // Cisco IOS-XE / NX-OS / Huawei / HPE / Dell
        ("gigabitethernet", "Gi"), // lowercase
        ("1GE", "Gi"), // Huawei explicit speed

        // ── 100M ──────────────────────────────────────────────────────────
        ("FastEthernet", "Fa"), // Cisco IOS (legacy)
        ("fastethernet", "Fa"), // lowercase
        ("100Base", "Fa"), // generic

        // ── Ethernet (generic / NX-OS / Arista / Brocade / Extreme) ───────
        // NOTE: "Ethernet" after all speed-specific prefixes are checked.
        // On NX-OS "Ethernet1/1" = any-speed; on Arista "Ethernet1" = 1G/10G/etc.
        // Both → "Eth" as a vendor-neutral short form.
        ("Ethernet", "Eth"),
        ("ethernet", "Eth"),

        // ── Management ────────────────────────────────────────────────────
        ("MgmtEth", "Mg"), // Cisco IOS-XR (explicit)
        ("Management", "Mg"), // Cisco IOS / Huawei (Ma on Arista)
        ("management", "Mg"), // lowercase
        ("Mgmt", "Mg"), // abbreviated form

        // ── Loopback ──────────────────────────────────────────────────────
        ("Loopback", "Lo"), // Cisco IOS / Huawei / Dell
        ("loopback", "Lo"), // lowercase / Arista

        // ── Tunnel ────────────────────────────────────────────────────────
        ("Tunnel", "Tu"), // Cisco IOS / Dell
        ("tunnel", "Tu"), // lowercase
        ("tunnel-te", "tt"), // Cisco IOS-XR traffic-engineering tunnel
        ("tunnel-ip", "ti"), // Cisco IOS-XR IP tunnel
        ("tunnel-mte", "tm"), // Cisco IOS-XR p2mp TE tunnel

        // ── LAG / Port-channel / Bundle ───────────────────────────────────
        ("Port-channel", "Po"), // Cisco IOS
        ("Port-Channel", "Po"), // Cisco NX-OS
        ("port-channel", "Po"), // lowercase
        ("portchannel", "Po"), // no hyphen variant
        ("Bundle-Ether", "BE"), // Cisco IOS-XR (canonical BE, not Po)
        ("bundle-ether", "BE"), // lowercase
        ("Eth-Trunk", "ET"), // Huawei (canonical ET)
        ("eth-trunk", "ET"), // lowercase

        // ── VLAN / SVI ────────────────────────────────────────────────────
        ("Vlan", "Vl"), // Cisco IOS SVI
        ("vlan", "Vl"), // NX-OS / lowercase
        ("BDI", "BDI"), // Cisco IOS Bridge Domain Interface (no shorter form)

        // ── Bridge / IRB ──────────────────────────────────────────────────
        ("BVI", "BVI"), // Cisco IOS-XR Bridge Virtual Interface
        // irb.x is Juniper — already canonical, handled by pass-through

        // ── VxLAN ─────────────────────────────────────────────────────────
        ("Vxlan", "Vx"), // Arista
        ("vxlan", "Vx"), // lowercase

        // ── Serial / WAN ──────────────────────────────────────────────────
        ("Serial", "Se"), // Cisco IOS WAN
        ("serial", "Se"), // lowercase
        ("Multilink", "Mu"), // Cisco IOS PPP multilink
        ("Dialer", "Di"), // Cisco IOS dialer
        ("Cellular", "Ce"), // Cisco LTE/5G modem
        ("HSSI", "Hs"), // Cisco high-speed serial (legacy)
        ("ATM", "At"), // Cisco ATM (legacy)
        ("Pos", "Po"), // Cisco Packet-over-SONET (conflicts with port-channel; keep last)

        // ── Other Cisco ───────────────────────────────────────────────────
        ("Virtual-Access", "Va"), // PPP virtual access
        ("Virtual-Template", "Vi"), // PPP virtual template
        ("Null", "Nu"), // Null interface (traffic blackhole)
        ("LISP", "Li"), // LISP virtual interface
        ("AppNav-Compress", "AC"), // WAN optimization
        ("Wlan-GigabitEthernet", "Wl-Gi"), // Embedded WLAN
        ("Service-Engine", "SE"), // ISR service module

        // ── Huawei specific ───────────────────────────────────────────────
        ("XGigabitEthernet", "XGi"), // Huawei 10G variant naming
        ("MEth", "Mg"), // Huawei management ethernet

        // ── HPE Comware ───────────────────────────────────────────────────
        ("M-GigabitEthernet", "Mg"), // HPE management

        // ── Brocade / Ruckus ICX ─────────────────────────────────────────
        ("StackedPort", "SP"), // Brocade stacking port

        // ── Dell OS10 ────────────────────────────────────────────────────
        ("port", "Po"), // Dell physical port (generic)

        // ── Extreme Networks ─────────────────────────────────────────────
        // Uses "port x" or "slot:port" — handled by pass-through
    ];
}