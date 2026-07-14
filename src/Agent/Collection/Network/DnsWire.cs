using System.Text;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// DNS wire-format name decoding (RFC 1035 §4.1.4 label compression), shared by the two DNS-based
/// scanners that parse untrusted multicast responses (review D22): <see cref="MdnsScanner" /> and
/// <see cref="LlmnrScanner" />. One hardened parser instead of two copies — one place to fuzz.
/// Bounds-checks every offset against <c>packet.Length</c> before indexing, and caps compression-
/// pointer hops (<see cref="MaxHops" />) to reject a pointer loop crafted to spin forever.
/// </summary>
public static class DnsWire
{
    private const int MaxHops = 128;

    /// <summary>
    /// Reads a (possibly compressed) DNS name starting at <paramref name="offset" />, advancing it
    /// past the name — or past the 2-byte pointer, if the name was reached via compression — so the
    /// caller can continue parsing the rest of the record. Returns as much of the name as could be
    /// decoded before a malformed length/pointer forced an early stop (never throws on bad input).
    /// </summary>
    public static string ReadName(byte[] packet, ref int offset)
    {
        StringBuilder sb = new();
        bool jumped = false;
        int origOffset = -1;
        int hops = 0;

        while (offset < packet.Length)
        {
            byte len = packet[offset];

            if (len == 0)
            {
                offset++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (offset + 1 >= packet.Length)
                {
                    break;
                }

                if (++hops > MaxHops)
                {
                    break; // compression pointer loop detected — stop
                }

                int ptr = ((len & 0x3F) << 8) | packet[offset + 1];

                if (!jumped)
                {
                    origOffset = offset + 2;
                }

                offset = ptr;
                jumped = true;
                continue;
            }

            offset++;

            if (offset + len > packet.Length)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            sb.Append(Encoding.ASCII.GetString(packet, offset, len));
            offset += len;
        }

        if (jumped && origOffset >= 0)
        {
            offset = origOffset;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Advances <paramref name="offset" /> past a DNS name without decoding it — used when the
    /// caller only needs to skip over a name (e.g. the question section) to reach subsequent
    /// fields. Unlike <see cref="ReadName" />, a compression pointer here always ends the name
    /// (RFC 1035: a pointer is only ever the last element of a name), so this doesn't need the
    /// hop guard.
    /// </summary>
    public static void SkipName(byte[] packet, ref int offset)
    {
        while (offset < packet.Length)
        {
            byte len = packet[offset];

            if (len == 0)
            {
                offset++;
                return;
            }

            if ((len & 0xC0) == 0xC0)
            {
                offset += 2;
                return;
            }

            offset += 1 + len;
        }
    }
}