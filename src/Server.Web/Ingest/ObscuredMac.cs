namespace JMW.Discovery.Server;

/// <summary>
/// Reconstructs the full MAC of a Google Wifi client from its obscured report value.
/// The firmware does NOT simply mask the last nibble — it preserves the real OUI
/// (first 3 bytes / 6 hex nibbles) and obfuscates the device-specific bytes, then
/// appends '*' (e.g. real "00:e0:bf:40:00:73" → reported "00e0bf1fc40*"). So the
/// only trustworthy part of the obscured value is the OUI; the device portion is
/// unusable for matching.
/// Reconstruction therefore joins on the station's IP: the server looks up the real
/// MAC it already knows for that IP (ARP / DHCP / prior discovery) and accepts it
/// only when its OUI matches the obscured OUI — which guards against a stale ARP
/// entry or a reassigned IP pointing at a different device. Pure logic; the
/// candidate MACs are supplied by the caller (queried from the DB).
/// </summary>
public static class ObscuredMac
{
    /// <summary>True when the value carries the firmware's '*' mask.</summary>
    public static bool IsObscured(string? mac) => mac is not null && mac.Contains('*');

    /// <summary>
    /// Extracts the real OUI — the first 3 bytes / 6 lowercase hex nibbles — from an
    /// obscured MAC. Returns false when fewer than 6 hex digits are present.
    /// </summary>
    public static bool TryGetOui(string mac, out string oui)
    {
        Span<char> buf = stackalloc char[mac.Length];
        int len = 0;
        foreach (char c in mac)
        {
            if (Uri.IsHexDigit(c))
            {
                buf[len++] = char.ToLowerInvariant(c);
                if (len == 6)
                {
                    break;
                }
            }
        }

        if (len < 6)
        {
            oui = string.Empty;
            return false;
        }

        oui = new string(buf[..6]);
        return true;
    }

    /// <summary>
    /// Chooses the full MAC for an obscured station from the real MACs the server has
    /// attested for the station's IP (12 lowercase hex, no separators). Keeps only
    /// candidates whose OUI matches the obscured OUI, and returns the result only when
    /// it is unique — otherwise null (no match, or conflicting evidence).
    /// </summary>
    public static string? Pick(IReadOnlyList<string?> ipMacs, string oui)
    {
        List<string> matches = ipMacs
            .OfType<string>()
            .Where(m => m.Length == 12 && m.StartsWith(oui, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}