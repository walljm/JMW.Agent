using System.Text.RegularExpressions;

namespace JMW.Discovery.Agent.Collection.Network.Recog;

/// <summary>A single &lt;param&gt; extraction rule. Pos 0 = static <see cref="Value"/>; Pos N = regex capture group N.</summary>
public sealed record RecogParam(int Pos, string Name, string? Value);

/// <summary>A &lt;example&gt; test vector: the input text plus the field values it is expected to extract.</summary>
public sealed record RecogExample(string Text, IReadOnlyDictionary<string, string> Expected, string? Encoding);

/// <summary>The result of matching an input: the fingerprint's description and its extracted fields.</summary>
public sealed record RecogMatch(string Description, IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// One Recog <c>&lt;fingerprint&gt;</c>: a compiled regex plus the parameter rules that turn a match
/// into typed fields. Implements Recog's extraction semantics — static values (pos 0), capture
/// groups (pos N), <c>{field}</c> interpolation, and <c>_tmp.</c> temporaries dropped from output.
/// </summary>
public sealed class RecogFingerprint
{
    private static readonly Regex Placeholder = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    private readonly Regex pattern;
    private readonly IReadOnlyList<RecogParam> parameters;

    public string PatternText { get; }
    public string Description { get; }
    public IReadOnlyList<RecogExample> Examples { get; }

    public RecogFingerprint(
        Regex pattern,
        string patternText,
        string description,
        IReadOnlyList<RecogParam> parameters,
        IReadOnlyList<RecogExample> examples
    )
    {
        this.pattern = pattern;
        PatternText = patternText;
        Description = description;
        this.parameters = parameters;
        Examples = examples;
    }

    /// <summary>Returns the extracted fields if <paramref name="input"/> matches this fingerprint, else null.</summary>
    public RecogMatch? Match(string input)
    {
        Match m = pattern.Match(input);
        return m.Success ? new RecogMatch(Description, Extract(m)) : null;
    }

    private Dictionary<string, string> Extract(Match m)
    {
        // Pass 1: resolve each param to a raw value (static or capture group). Keep _tmp.* here
        // because interpolation may reference them.
        Dictionary<string, string> raw = new(StringComparer.Ordinal);
        foreach (RecogParam p in parameters)
        {
            string? value = p.Pos == 0
                ? p.Value
                : p.Pos < m.Groups.Count && m.Groups[p.Pos].Success ? m.Groups[p.Pos].Value : null;

            if (value is not null)
            {
                raw[p.Name] = value;
            }
        }

        // Pass 2: substitute {field} placeholders from resolved values (iterate for chained refs).
        for (int pass = 0; pass < 5; pass++)
        {
            bool changed = false;
            foreach (string key in raw.Keys.ToList())
            {
                string value = raw[key];
                if (!value.Contains('{', StringComparison.Ordinal))
                {
                    continue;
                }

                string replaced = Placeholder.Replace(
                    value,
                    mt => raw.TryGetValue(mt.Groups[1].Value, out string? r)
                       && !r.Contains('{', StringComparison.Ordinal)
                            ? r
                            : mt.Value
                );
                if (!string.Equals(replaced, value, StringComparison.Ordinal))
                {
                    raw[key] = replaced;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        // Pass 3: emit, dropping temporaries and any value with an unresolved placeholder (Recog drops these).
        Dictionary<string, string> output = new(StringComparer.Ordinal);
        foreach ((string key, string value) in raw)
        {
            if (key.StartsWith("_tmp.", StringComparison.Ordinal)
             || value.Contains('{', StringComparison.Ordinal))
            {
                continue;
            }

            output[key] = value;
        }

        return output;
    }
}