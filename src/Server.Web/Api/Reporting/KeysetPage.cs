using JMW.Discovery.Server.Api;

namespace JMW.Discovery.Server.Reporting;

/// <summary>
/// Shared keyset-pagination plumbing for the reporting list endpoints (review D3). Two seams:
/// <see cref="RunAsync{TItem}" /> is the endpoint-handler shell (clamp → decode-or-422 → fetch →
/// <c>{items, next_cursor}</c>); <see cref="NextCursor{TRow}" /> is the fetch-side tail that trims
/// the (limit + 1) sentinel row and encodes the next cursor. The tail lives here because it is
/// shared by both the API handler and the Razor page code-behind, which each call the endpoint's
/// <c>QueryAsync</c> directly.
/// </summary>
public static class KeysetPage
{
    /// <summary>
    /// Runs the standard reporting-endpoint shell: clamp <paramref name="limit" /> to
    /// [1, <paramref name="maxLimit" />], decode <paramref name="after" /> into
    /// <paramref name="cursorArity" /> parts (returning 422 <c>invalid_cursor</c> on failure), invoke
    /// <paramref name="fetch" /> with the decoded parts (null when there is no cursor), and wrap the
    /// result as <c>{ items, next_cursor }</c>. <paramref name="fetch" /> owns the actual query and
    /// the trim/encode of the next cursor (typically via <see cref="NextCursor{TRow}" />).
    /// </summary>
    public static async Task<IResult> RunAsync<TItem>(
        string? after,
        int limit,
        int maxLimit,
        int cursorArity,
        Func<string[]?, int, Task<(List<TItem> Items, string? NextCursor)>> fetch
    )
    {
        limit = Math.Clamp(limit, 1, maxLimit);

        string[]? parts = null;
        if (!string.IsNullOrEmpty(after))
        {
            if (!KeysetCursor.TryDecodeParts(after, cursorArity, out string[] decoded))
            {
                return ApiError.InvalidCursor();
            }

            parts = decoded;
        }

        (List<TItem> items, string? nextCursor) = await fetch(parts, limit);

        return Results.Ok(
            new
            {
                items,
                next_cursor = nextCursor,
            }
        );
    }

    /// <summary>
    /// Given rows fetched with a (limit + 1) over-read, drops the sentinel row in place when present
    /// and returns the encoded cursor for the last surviving row, or null when there is no next page.
    /// <paramref name="cursorOf" /> selects the keyset tuple from a row.
    /// </summary>
    public static string? NextCursor<TRow>(List<TRow> rows, int limit, Func<TRow, string[]> cursorOf)
    {
        if (rows.Count <= limit)
        {
            return null;
        }

        rows.RemoveAt(rows.Count - 1);
        return KeysetCursor.EncodeParts(cursorOf(rows[^1]));
    }
}