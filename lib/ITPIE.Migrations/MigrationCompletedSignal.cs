namespace ITPIE.Migrations;

/// <summary>
/// Signals when database migrations have completed so dependent hosted services can wait.
/// </summary>
public sealed class MigrationCompletedSignal
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Awaitable task that completes when migrations finish (or are skipped).</summary>
    public Task Completed => _tcs.Task;

    internal void SetCompleted() => _tcs.TrySetResult();
    internal void SetFailed(Exception ex) => _tcs.TrySetException(ex);
}