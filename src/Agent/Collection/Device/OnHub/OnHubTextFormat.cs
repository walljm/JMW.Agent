using System.Globalization;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Device.OnHub;

/// <summary>
/// A single node in a protobuf text-format tree. Either a scalar
/// (<see cref="Value" /> set, <see cref="Children" /> empty) or a message
/// (<see cref="Children" /> set, <see cref="Value" /> null).
/// </summary>
public sealed class TextNode
{
    public TextNode(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>Scalar value with quotes/escapes resolved, or null for a message node.</summary>
    public string? Value { get; set; }

    public List<TextNode> Children { get; } = [];

    /// <summary>All immediate children with the given name (protobuf allows repeats).</summary>
    public IEnumerable<TextNode> ChildrenNamed(string name) =>
        Children.Where(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>Scalar value of the first immediate child with the given name, or null.</summary>
    public string? ScalarOf(string name) =>
        Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))?.Value;
}

/// <summary>
/// Minimal recursive-descent parser for the protobuf text format that Google Wifi
/// OnHub emits (the <c>networkState</c> blob and the <c>ap-show</c> command output).
/// Grammar handled:
/// body    := field*
/// field   := IDENT ( ':' scalar | ['+':'] '{' body '}' )
/// scalar  := QUOTED | BAREWORD          (bool / number / enum / unquoted token)
/// QUOTED  := '"' ( '\' any | not-'"' )* '"'
/// It is deliberately lenient: unexpected characters are skipped rather than
/// throwing, because the input is machine-generated device output we only mine a
/// handful of fields from — a parse hiccup should degrade to "fewer stations", not
/// a failed collection cycle.
/// </summary>
public static class OnHubTextFormat
{
    public static IReadOnlyList<TextNode> Parse(string text)
    {
        int pos = 0;
        return ParseBody(text, ref pos, topLevel: true);
    }

    private static List<TextNode> ParseBody(string s, ref int pos, bool topLevel)
    {
        List<TextNode> nodes = [];
        while (pos < s.Length)
        {
            SkipTrivia(s, ref pos);
            if (pos >= s.Length)
            {
                break;
            }

            if (s[pos] == '}')
            {
                if (topLevel)
                {
                    // Stray close brace at top level — skip and continue.
                    pos++;
                    continue;
                }

                pos++; // consume the closing brace of this message
                break;
            }

            if (!IsIdentStart(s[pos]))
            {
                // Not the start of a field — skip one char and resync.
                pos++;
                continue;
            }

            string name = ReadIdent(s, ref pos);
            SkipInlineWhitespace(s, ref pos);

            bool sawColon = false;
            if (pos < s.Length && s[pos] == ':')
            {
                sawColon = true;
                pos++;
                SkipInlineWhitespace(s, ref pos);
            }

            TextNode node = new(name);

            if (pos < s.Length && s[pos] == '{')
            {
                // Message value (colon optional before the brace).
                pos++;
                node.Children.AddRange(ParseBody(s, ref pos, topLevel: false));
            }
            else if (sawColon)
            {
                node.Value = ReadScalar(s, ref pos);
            }
            else
            {
                // Bare identifier with neither ':' nor '{' — nothing to attach; skip.
                continue;
            }

            nodes.Add(node);
        }

        return nodes;
    }

    private static string ReadScalar(string s, ref int pos)
    {
        if (pos < s.Length && s[pos] == '"')
        {
            return ReadQuoted(s, ref pos);
        }

        int start = pos;
        while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] != '{' && s[pos] != '}')
        {
            pos++;
        }

        return s[start..pos];
    }

    private static string ReadQuoted(string s, ref int pos)
    {
        pos++; // opening quote

        // Accumulate BYTES, not chars: protobuf text format emits non-ASCII bytes as
        // consecutive octal escapes (e.g. U+2019 "’" → \342\200\231), so a multibyte
        // UTF-8 sequence must be reassembled at the byte level, then decoded once.
        List<byte> bytes = [];
        while (pos < s.Length)
        {
            char c = s[pos++];
            if (c == '"')
            {
                break;
            }

            if (c == '\\' && pos < s.Length)
            {
                char e = s[pos];
                if (e is >= '0' and <= '7')
                {
                    // Octal escape: up to 3 octal digits → one byte.
                    int val = 0, n = 0;
                    while (n < 3 && pos < s.Length && s[pos] is >= '0' and <= '7')
                    {
                        val = (val * 8) + (s[pos++] - '0');
                        n++;
                    }

                    bytes.Add((byte)val);
                    continue;
                }

                pos++; // consume the escape char
                if (e is 'x' or 'X')
                {
                    // Hex escape: up to 2 hex digits → one byte.
                    int val = 0, n = 0;
                    while (n < 2 && pos < s.Length && Uri.IsHexDigit(s[pos]))
                    {
                        val = (val * 16) + Convert.ToInt32(s[pos++].ToString(), 16);
                        n++;
                    }

                    bytes.Add((byte)val);
                    continue;
                }

                char resolved = e switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'a' => '\a',
                    'b' => '\b',
                    'f' => '\f',
                    'v' => '\v',
                    _ => e, // '"', '\\', '\'', '?', and any unknown escape → literal
                };
                bytes.AddRange(Encoding.UTF8.GetBytes([resolved]));
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes([c]));
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static void SkipTrivia(string s, ref int pos)
    {
        while (pos < s.Length)
        {
            char c = s[pos];
            if (char.IsWhiteSpace(c))
            {
                pos++;
            }
            else if (c == '#')
            {
                // Comment to end of line (text format allows '#').
                while (pos < s.Length && s[pos] != '\n')
                {
                    pos++;
                }
            }
            else
            {
                break;
            }
        }
    }

    private static void SkipInlineWhitespace(string s, ref int pos)
    {
        while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t'))
        {
            pos++;
        }
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    private static string ReadIdent(string s, ref int pos)
    {
        int start = pos;
        while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_'))
        {
            pos++;
        }

        return s[start..pos];
    }

    /// <summary>Parses an integer token from OnHub command output, or null if unparseable
    /// (review D31 — was re-declared identically in <c>OnHubApInterfaces</c> and
    /// <c>OnHubStations</c>).</summary>
    public static long? ParseLong(string s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
}