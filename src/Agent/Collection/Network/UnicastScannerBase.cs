namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Base for unicast network scanners that probe every on-link neighbor independently. Encapsulates the
/// fan-out shell — bounded-concurrency probe of each neighbor, then drop the non-responders — that was
/// copy-pasted across ~10 scanners (review D5). Subclasses supply <see cref="Name" />, optionally
/// override <see cref="MaxConcurrency" />, and implement the per-host <see cref="ProbeHostAsync" />.
/// Neighbors come from the collector's shared table (<see cref="NeighborTable" />, review D1) — the base
/// never reads the ARP/ND table itself.
/// </summary>
public abstract class UnicastScannerBase : INetworkScanner
{
    public abstract string Name { get; }

    public virtual bool IsSupported => true;

    /// <summary>Maximum concurrent per-host probes. Defaults to 30.</summary>
    protected virtual int MaxConcurrency => 30;

    /// <summary>
    /// Probes a single host, returning the discovered device or null if the host does not respond to
    /// this scanner's protocol. Must not throw for an unreachable host — swallow and return null.
    /// </summary>
    protected abstract Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct);

    public async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(NetworkScanTarget target, CancellationToken ct)
    {
        SemaphoreSlim semaphore = new(MaxConcurrency);

        List<Task<DiscoveredDevice?>> tasks = new(target.Neighbors.Count);
        foreach (Neighbor neighbor in target.Neighbors)
        {
            tasks.Add(ProbeWithLimitAsync(neighbor.Ip.ToString(), semaphore, ct));
        }

        DiscoveredDevice?[] results = await Task.WhenAll(tasks);
        return results.OfType<DiscoveredDevice>().ToList();
    }

    private async Task<DiscoveredDevice?> ProbeWithLimitAsync(string ip, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            return await ProbeHostAsync(ip, ct);
        }
        finally
        {
            semaphore.Release();
        }
    }
}