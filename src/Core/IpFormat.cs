using System.Net;

namespace JMW.Discovery.Core;

/// <summary>
/// Canonical IP-address formatting, shared by the IP value normalizer and ingest key normalization
/// so an IP is byte-identical whether it appears as a fact value or a dimension key. Canonical form
/// is <see cref="IPAddress" />'s: IPv4 without leading zeros, IPv6 lowercase + compressed. An
/// optional CIDR suffix ("192.168.1.5/24") is preserved.
/// </summary>
public static class IpFormat
{
    /// <summary>
    /// The canonical form of <paramref name="raw" /> (CIDR suffix preserved), or null if it
    /// is not an IP address — callers decide whether to pass such values through unchanged.
    /// </summary>
    public static string? Canonicalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();
        int slash = trimmed.IndexOf('/');
        string addr = slash >= 0 ? trimmed[..slash] : trimmed;
        if (!IPAddress.TryParse(addr, out IPAddress? ip))
        {
            return null;
        }

        return slash >= 0 ? ip + trimmed[slash..] : ip.ToString();
    }
}