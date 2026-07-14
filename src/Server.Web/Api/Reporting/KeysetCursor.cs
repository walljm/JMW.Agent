using System.Text;

namespace JMW.Discovery.Server.Reporting;

/// <summary>
/// Opaque keyset cursor for hostname-ordered reporting endpoints.
/// Encodes the (sortKey, deviceId) tuple that drives the keyset comparison.
/// The sortKey is the hostname collated with COALESCE(hostname,'') so rows with
/// a null hostname remain reachable across pages.
/// Format: base64(sortKey + "" + deviceId).
/// </summary>
public static class KeysetCursor
{
    private const char Separator = '';

    public static string Encode(string sortKey, string deviceId)
    {
        string raw = $"{sortKey}{Separator}{deviceId}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    public static bool TryDecode(string? cursor, out string sortKey, out string deviceId)
    {
        sortKey = string.Empty;
        deviceId = string.Empty;

        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            int sep = decoded.IndexOf(Separator);
            if (sep < 0)
            {
                return false;
            }

            sortKey = decoded[..sep];
            deviceId = decoded[(sep + 1)..];
            return Guid.TryParse(deviceId, out _);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Encodes an arbitrary tuple of string parts into an opaque cursor.
    /// Use for keyset endpoints whose sort tuple is not (string, GUID).
    /// Parts are joined with the unit-separator control character, which never
    /// appears in the encoded values (numeric ticks, device keys, IP/port text).
    /// </summary>
    public static string EncodeParts(params string[] parts)
    {
        string raw = string.Join(Separator, parts);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>
    /// Decodes a cursor produced by <see cref="EncodeParts" /> into its parts.
    /// Returns false on malformed base64 or when the part count does not match
    /// <paramref name="expectedCount" />.
    /// </summary>
    public static bool TryDecodeParts(string? cursor, int expectedCount, out string[] parts)
    {
        parts = Array.Empty<string>();

        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            string[] split = decoded.Split(Separator);
            if (split.Length != expectedCount)
            {
                return false;
            }

            parts = split;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}