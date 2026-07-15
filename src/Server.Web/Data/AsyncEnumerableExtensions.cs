namespace JMW.Discovery.Server.Queries;

/// <summary>
/// Helpers for consuming IAsyncEnumerable results from generated database commands.
/// </summary>
internal static class AsyncEnumerableExtensions
{
    /// <summary>
    /// Executes a generated database command that returns no meaningful rows
    /// (pure INSERT / UPDATE / DELETE without RETURNING). Enumerates the
    /// sequence to drive execution, then discards all results.
    /// </summary>
    public static async Task ExecuteAsync<T>(
        this IAsyncEnumerable<T> source,
        CancellationToken cancellationToken = default
    )
    {
        await foreach (T _ in source.WithCancellation(cancellationToken))
        {
            // intentionally empty — we only need to drive execution
        }
    }
}