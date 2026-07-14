namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Parses <c>key=value</c> lines (shell-style config: <c>/etc/os-release</c>, <c>lsb_release</c>
/// output, etc.) into a case-insensitive dictionary — re-declared per-collector (review D28).
/// Lines with no <c>=</c> are skipped; the value is trimmed of whitespace and one layer of
/// surrounding double quotes.
/// </summary>
public static class KeyValueParser
{
    public static Dictionary<string, string> ParseEqualsKeyValue(IEnumerable<string> lines)
    {
        Dictionary<string, string> kv = new(StringComparer.OrdinalIgnoreCase);

        foreach (string line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            kv[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
        }

        return kv;
    }
}