namespace JMW.Discovery.Core;

/// <summary>
/// One component of a fact's dotted path.
/// "Interface[eth0]"  →  Name = "Interface",  Key = "eth0"   (list element)
/// "Speed"            →  Name = "Speed",       Key = null     (singleton / attribute)
/// </summary>
public readonly record struct FactSegment(string Name, string? Key)
{
    // ── Input bounds ──────────────────────────────────────────────────────────
    //
    // These caps prevent a malicious or misconfigured device from causing
    // unbounded allocation via pathologically large fact IDs.
    //
    // MaxIdLength:    512 chars covers any realistic path
    //                 ("Device[uuid].Interface[mac].SubComponent[id].Attribute")
    //                 An 80K-device network with 1000 facts/device never approaches this.
    //
    // MaxSegments:    32 segments. Real network paths top out at 5–6.
    //                 32 is generous enough for any legitimate nesting depth.
    //
    // Both limits are checked in ParsePath — the single entry point — before
    // any List allocation or string copying occurs.

    public const int MaxIdLength = 512;
    public const int MaxSegments = 32;

    public bool IsList => Key is not null;

    public override string ToString() => Key is not null ? $"{Name}[{Key}]" : Name;

    /// <summary>
    /// Parses a full fact path into segments.
    /// Splits on '.' only when outside brackets, so keys containing dots
    /// (IP addresses, CIDR prefixes, router-ids) are preserved intact.
    /// Throws <see cref="ArgumentOutOfRangeException" /> if the ID exceeds
    /// <see cref="MaxIdLength" /> characters or produces more than
    /// <see cref="MaxSegments" /> segments. Both checks fire before any
    /// allocation so a malicious payload cannot exhaust memory.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// ID is longer than MaxIdLength or parses to more than MaxSegments segments.
    /// </exception>
    public static FactSegment[] ParsePath(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return [];
        }

        if (id.Length > MaxIdLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(id),
                $"Fact ID length {id.Length} exceeds maximum of {MaxIdLength} characters."
            );
        }

        List<FactSegment> results = new(4);
        int depth = 0;
        int start = 0;

        for (int i = 0; i <= id.Length; i++)
        {
            char c = i < id.Length ? id[i] : '.'; // sentinel — flush final token
            switch (c)
            {
                case '[': depth++; break;
                case ']': depth--; break;
                case '.' when depth == 0:
                    AddSegment(id.AsSpan(start, i - start), results);
                    start = i + 1;

                    if (results.Count > MaxSegments)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(id),
                            $"Fact ID has more than {MaxSegments} path segments."
                        );
                    }

                    break;
            }
        }

        return [.. results];
    }

    private static void AddSegment(ReadOnlySpan<char> token, List<FactSegment> results)
    {
        int open = token.IndexOf('[');
        int close = open >= 0 ? token.LastIndexOf(']') : -1;

        results.Add(
            open >= 0 && close > open
                ? new FactSegment(token[..open].ToString(), token[(open + 1)..close].ToString())
                : new FactSegment(token.ToString(), null)
        );
    }
}