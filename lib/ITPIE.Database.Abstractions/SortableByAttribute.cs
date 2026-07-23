namespace ITPIE.Database.Abstractions;

/// <summary>
/// Declares one user-selectable sort column on a <see cref="DatabaseCommandAttribute" /> method.
/// The generator emits one command-text variant per declared column per direction (asc/desc),
/// substituting the tokens <c>__SORT_KEY__</c> (the <paramref name="sqlExpression" />),
/// <c>__CMP__</c> (<c>&gt;</c> asc / <c>&lt;</c> desc, for the keyset row comparison), and
/// <c>__DIR__</c> (<c>ASC</c>/<c>DESC</c>) in the command text file — and one schema validation
/// per variant, so every sort/direction combination of a keyset query is checked against the
/// live schema exactly like a plain command.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///         The method must additionally declare <c>string? sort</c> and <c>string? dir</c>
///         parameters (matched by name). They select the variant at runtime and are NOT bind
///         parameters — they never reach SQL as values, so the placeholder count excludes them.
///         An unrecognized <c>sort</c> falls back to the FIRST declared column (the default);
///         <c>dir</c> is descending only when it equals "desc" (case-insensitive).
///         </item>
///         <item>
///         <paramref name="sqlExpression" /> is trusted SQL text (an allowlist entry authored
///         next to the query, never user input) — identifiers and expressions cannot be bound
///         as parameters, which is the entire reason this attribute exists.
///         </item>
///         <item>
///         Declare keys unique per method; the command text must contain all three tokens.
///         </item>
///     </list>
/// </remarks>
/// <param name="key">The stable API/UI token for this sort column (e.g. "hostname").</param>
/// <param name="sqlExpression">The SQL sort expression (e.g. "coalesce(pdv.hostname, '')").</param>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
public sealed class SortableByAttribute(string key, string sqlExpression) : Attribute
{
    /// <summary>Gets the stable API/UI token for this sort column.</summary>
    public string Key { get; } = key;

    /// <summary>Gets the SQL sort expression substituted for <c>__SORT_KEY__</c>.</summary>
    public string SqlExpression { get; } = sqlExpression;
}