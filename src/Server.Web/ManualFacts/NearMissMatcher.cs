using System.Text;

namespace JMW.Discovery.Server.ManualFacts;

/// <summary>
/// Advisory near-miss detection for arbitrary fact paths (REQ-003, architecture §6): when an
/// operator types a path that is not in the catalog but is a close match to exactly one catalog
/// template, surface it as a "did you mean to override X?" suggestion. Purely advisory — the
/// operator can always proceed with either interpretation. Runs in-memory over the ~450 short
/// catalog strings per submit; no dependency.
/// </summary>
public static class NearMissMatcher
{
    /// <summary>
    /// Returns the single catalog template a submitted arbitrary <paramref name="template" /> is a
    /// near-miss of, or null when zero or more than one candidate qualifies (both cases mean "treat
    /// as genuinely arbitrary"). <paramref name="candidates" /> is the full catalog template set.
    /// </summary>
    public static string? FindSuggestion(string template, IReadOnlyList<string> candidates)
    {
        string normalizedInput = Normalize(template);
        if (normalizedInput.Length == 0)
        {
            return null;
        }

        // Threshold scales with length so short paths need a near-exact match while longer ones
        // tolerate a couple of edits. A missing "[]" (the common dimensionality typo) normalizes
        // away entirely, so a real catalog path is distance 0 and always the single suggestion.
        int threshold = Math.Max(2, (int)Math.Ceiling(0.15 * normalizedInput.Length));

        string? match = null;
        foreach (string candidate in candidates)
        {
            if (Levenshtein(normalizedInput, Normalize(candidate)) > threshold)
            {
                continue;
            }

            if (match is not null)
            {
                return null; // more than one qualifies → genuinely arbitrary
            }

            match = candidate;
        }

        return match;
    }

    // Strip the leading "Device[]." scope prefix, drop brackets/punctuation, lowercase — so a
    // dimensionality typo or casing difference collapses to the same normalized string.
    private static string Normalize(string path)
    {
        ReadOnlySpan<char> span = path.AsSpan();
        if (span.StartsWith("Device[]."))
        {
            span = span["Device[].".Length..];
        }

        StringBuilder sb = new(span.Length);
        foreach (char c in span)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        int[] prev = new int[b.Length + 1];
        int[] curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }
}
