using System.Globalization;
using System.Text;

namespace JMW.Discovery.Server.UI;

/// <summary>
/// Shared presentation-layer formatters for Razor views and page models.
/// Consolidates helpers that were previously copy-pasted per page (e.g. byte sizes).
/// View-specific CSS-class mappers stay with their pages; this holds only the
/// domain-agnostic string formatting that every view needs.
/// </summary>
public static class ViewFormat
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Human-readable byte size, e.g. 1536 → "1.5 KB". Null → "—".</summary>
    public static string Bytes(long? bytes)
    {
        if (bytes is null)
        {
            return "—";
        }

        double v = bytes.Value;
        int u = 0;
        while (v >= 1024 && u < ByteUnits.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return $"{v:0.#} {ByteUnits[u]}";
    }

    /// <summary>Compact duration from seconds, e.g. 3661 → "1h1m". Null or ≤0 → "".</summary>
    public static string Duration(long? secs)
    {
        if (!secs.HasValue || secs.Value <= 0)
        {
            return "";
        }

        long total = secs.Value;
        long d = total / 86400;
        total -= d * 86400;
        long h = total / 3600;
        total -= h * 3600;
        long m = total / 60;
        total -= m * 60;
        long s = total;

        StringBuilder parts = new();
        if (d > 0) { parts.Append(d).Append('d'); }

        if (h > 0) { parts.Append(h).Append('h'); }

        if (m > 0) { parts.Append(m).Append('m'); }

        if (s > 0) { parts.Append(s).Append('s'); }

        return parts.ToString();
    }

    /// <summary>Relative "time ago" string from a UTC timestamp, e.g. "5m ago". Future → "0s ago".</summary>
    public static string RelativeTime(DateTime utc)
    {
        TimeSpan delta = DateTime.UtcNow - utc;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalSeconds < 60)
        {
            return $"{(int)delta.TotalSeconds}s ago";
        }

        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes}m ago";
        }

        if (delta.TotalHours < 24)
        {
            return $"{(int)delta.TotalHours}h ago";
        }

        return $"{(int)delta.TotalDays}d ago";
    }

    /// <summary>
    /// ISO-8601 UTC instant for a `data-utc` attribute, e.g. `&lt;time data-utc="@ViewFormat.IsoUtc(x)"&gt;`.
    /// `local-time.js` reads this on page load and rewrites the element to the viewer's local
    /// timezone, so every timestamp is authored server-side in UTC and localized client-side.
    /// </summary>
    public static string IsoUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Truncates to <paramref name="max" /> characters with a trailing ellipsis.</summary>
    public static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    /// <summary>
    /// Classifies a MAC address by the first octet's I/G and U/L bits into a display
    /// badge: (Label, Title, CssClass). Multicast (I/G set), locally administered
    /// (U/L set), or universal (burned-in). Null/unparseable → ("—","","").
    /// Shared so every MAC-displaying view (device detail, hosts, ARP) flags MACs consistently.
    /// The U/L bit alone cannot distinguish OS-level privacy randomization from a
    /// hypervisor/container-assigned static address — both set the same bit — so the
    /// label says "local" rather than "randomized", which would overclaim.
    /// </summary>
    public static (string Label, string Title, string Css) MacFlag(string? mac)
    {
        if (string.IsNullOrEmpty(mac))
        {
            return ("—", "", "");
        }

        string hex = mac.Replace(":", "").Replace("-", "");
        if (hex.Length < 2 || !byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, null, out byte b))
        {
            return ("—", "", "");
        }

        if ((b & 0x01) != 0)
        {
            return ("multicast", "Group/multicast address (I/G bit set)", "mac-flag-multicast");
        }

        if ((b & 0x02) != 0)
        {
            return ("local", "Locally administered (U/L bit set) — assigned by software rather than burned in by " +
                "the hardware vendor. Common for VMs, containers, VPN adapters, and OS-level MAC-privacy " +
                "features; a single address can't distinguish which.", "mac-flag-local");
        }

        return ("universal", "Universally administered — burned-in hardware address", "mac-flag-universal");
    }

    /// <summary>
    /// Formats a MAC for display: bare 12-hex (the canonical stored form) → colon-separated
    /// "aa:bb:cc:dd:ee:ff". Values that aren't exactly 12 hex chars (already-colon legacy rows,
    /// null) pass through unchanged (or "—" when empty), so it is safe during the bare-hex cutover.
    /// </summary>
    public static string FormatMac(string? mac)
    {
        if (string.IsNullOrEmpty(mac))
        {
            return "—";
        }

        if (mac.Length != 12)
        {
            return mac;
        }

        return string.Join(':', Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));
    }

    /// <summary>Best-effort OS label: distro if known, else family, else "—".</summary>
    public static string FormatOs(string? family, string? distro)
    {
        if (!string.IsNullOrEmpty(distro))
        {
            return distro;
        }

        return string.IsNullOrEmpty(family) ? "—" : family;
    }

    /// <summary>
    /// Formats the OUI-derived NIC vendor with its ISO 3166-1 alpha-2 registration
    /// country appended, e.g. "Cisco Systems, Inc (US)". Null vendor → "—"; a vendor
    /// with no resolved country (older IEEE registry rows, best-effort extraction) →
    /// the vendor name alone.
    /// </summary>
    public static string FormatOui(string? vendor, string? countryCode) =>
        string.IsNullOrEmpty(vendor) ? "—"
        : string.IsNullOrEmpty(countryCode) ? vendor
        : $"{vendor} ({countryCode})";
}