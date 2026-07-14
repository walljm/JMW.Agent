namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Parses CRLF-delimited RFC 822 style "Name: value" header lines, as seen in the SSDP and RTSP
/// responses these scanners read (both are HTTP-derived text protocols). The leading status/request
/// line is skipped implicitly since it never contains a colon; duplicate header names keep the last
/// value (review D32 — was re-declared near-identically in <c>SsdpScanner</c> and <c>RtspScanner</c>).
/// </summary>
public static class HttpHeaderLines
{
    public static Dictionary<string, string> Parse(string response)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

        foreach (string line in response.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return headers;
    }
}