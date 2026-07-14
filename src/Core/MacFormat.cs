namespace JMW.Discovery.Core;

/// <summary>
/// The one canonical MAC utility, shared across the whole system (Core + Agent). It owns the strip
/// logic once and exposes two forms:
/// <list type="bullet">
///     <item>
///     <see cref="ToBareHex" /> — the canonical storage form: bare 12-hex lowercase. Both the
///     fingerprint side (<see cref="FingerprintNormalizer.NormalizeMac" />) and the fact normalizers run
///     through this, so a MAC canonicalized as a fingerprint is byte-identical to the same MAC as a fact
///     (review D34/D2). Accepts any separator style, and the macOS case where an octet drops its leading
///     zero ("0:11:22:33:44:5"). Callers layer their own policy on top (identity rejection, all-zero).
///     </item>
///     <item><see cref="Normalize" /> — the colon display form ("aa:bb:cc:dd:ee:ff").</item>
/// </list>
/// </summary>
public static class MacFormat
{
    /// <summary>
    /// Canonical bare 12-hex lowercase, or null if the input is not a MAC. Handles colon/dash/dot
    /// separators and bare hex, plus the macOS zero-dropped-octet form ("0:11:22:33:44:5"). No semantic
    /// rejection (all-zero, multicast, locally-administered) — that is the caller's policy.
    /// </summary>
    public static string? ToBareHex(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Fast path: strip ':'/'-'/'.' and require exactly 12 hex digits.
        Span<char> buf = stackalloc char[12];
        int len = 0;
        bool clean = true;
        foreach (char c in raw.AsSpan().Trim())
        {
            if (c is ':' or '-' or '.')
            {
                continue;
            }

            char lower = char.ToLowerInvariant(c);
            if (!IsHexDigit(lower) || len == 12)
            {
                clean = false; // non-hex char, or more than 12 hex digits
                break;
            }

            buf[len++] = lower;
        }

        if (clean && len == 12)
        {
            return new string(buf);
        }

        // Fallback: colon-delimited octets that dropped their leading zero (macOS "0:11:22:33:44:5").
        return PadColonOctetsToBare(raw);
    }

    /// <summary>
    /// Colon-separated lowercase display form ("aa:bb:cc:dd:ee:ff"), or the lowercased input
    /// unchanged when it is not a MAC.
    /// </summary>
    public static string Normalize(string raw) =>
        ToBareHex(raw) is { } bare ? Colonize(bare) : raw.ToLowerInvariant();

    /// <summary>
    /// Colon-separated lowercase display form from raw address bytes (e.g. an SNMP
    /// <c>PhysAddress</c> or <see cref="System.Net.NetworkInformation.PhysicalAddress" />), was
    /// re-declared per-collector (review D32). Formats however many bytes are given — callers own
    /// any length validation (a MAC is 6 bytes, but this doesn't assume it).
    /// </summary>
    public static string FromBytes(byte[] bytes) => string.Join(':', bytes.Select(b => b.ToString("x2")));

    // Six colon-delimited octets of 1–2 hex chars, each padded to two → bare 12-hex. Null otherwise.
    private static string? PadColonOctetsToBare(string raw)
    {
        Span<char> buf = stackalloc char[12];
        int pos = 0;
        int octet = 0;
        int start = 0;

        for (int i = 0; i <= raw.Length; i++)
        {
            if (i != raw.Length && raw[i] != ':')
            {
                continue;
            }

            int octLen = i - start;
            if (octet == 6 || octLen is < 1 or > 2)
            {
                return null;
            }

            char hi = octLen == 2 ? ToHexLower(raw[start]) : '0';
            char lo = ToHexLower(raw[i - 1]);
            if (hi == '\0' || lo == '\0')
            {
                return null;
            }

            buf[pos++] = hi;
            buf[pos++] = lo;
            octet++;
            start = i + 1;
        }

        return octet == 6 ? new string(buf) : null;
    }

    private static string Colonize(ReadOnlySpan<char> bareHex12)
    {
        Span<char> result = stackalloc char[17];
        int pos = 0;
        for (int i = 0; i < 6; i++)
        {
            if (i > 0)
            {
                result[pos++] = ':';
            }

            result[pos++] = bareHex12[i * 2];
            result[pos++] = bareHex12[(i * 2) + 1];
        }

        return new string(result);
    }

    private static bool IsHexDigit(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static char ToHexLower(char c) => c switch
    {
        >= '0' and <= '9' => c,
        >= 'a' and <= 'f' => c,
        >= 'A' and <= 'F' => (char)(c + 32),
        _ => '\0',
    };
}