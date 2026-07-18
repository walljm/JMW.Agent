using System.Collections.Concurrent;

namespace JMW.Discovery.Server.Infrastructure;

/// <summary>
/// One page of agent log output received from an on-demand pull. Deliberately transient —
/// held only in <see cref="AgentLogCache" />, never written to Postgres (the log TEXT is
/// operational detail we surface once for an admin, not something we archive; see
/// docs/plans/agent-log-viewer.md §2/§4.3).
/// </summary>
/// <param name="RequestedAt">The <c>logs_requested_at</c> timestamp this page answers.</param>
/// <param name="ReceivedAt">When the server received the upload (drives TTL eviction).</param>
/// <param name="Source"><c>journald</c> or <c>buffer</c> — which capture source produced the text.</param>
/// <param name="Truncated">True if the agent capped the page by byte ceiling mid-content.</param>
/// <param name="Text">The captured log lines, newest-first, exactly as the agent read them.</param>
/// <param name="NextBeforeToken">Opaque paging token for the next older page, or null when the source is exhausted.</param>
public sealed record AgentLogBundle(
    DateTimeOffset RequestedAt,
    DateTimeOffset ReceivedAt,
    string Source,
    bool Truncated,
    string Text,
    string? NextBeforeToken
);

/// <summary>
/// In-memory, per-agent cache of the most recently received log page. Singleton. Holds exactly
/// one bundle per agent (the latest page — pages are stitched together client-side, not here;
/// see docs/plans/agent-log-viewer.md §4.3). A periodic <see cref="AgentLogCacheSweepService" />
/// evicts stale entries. Everything here is gone on server restart, which is fine: this is a
/// "look at it now" feature, not an archive. No log content ever reaches the database.
/// </summary>
public sealed class AgentLogCache
{
    /// <summary>Evict a bundle once it's older than this (by <see cref="AgentLogBundle.ReceivedAt" />).</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    /// <summary>Hard cap on cached agents, so a large/forgotten fleet can't grow this unbounded.</summary>
    public const int MaxEntries = 200;

    private readonly ConcurrentDictionary<Guid, AgentLogBundle> _bundles = new();

    public void Set(Guid agentId, AgentLogBundle bundle) => _bundles[agentId] = bundle;

    public bool TryGet(Guid agentId, out AgentLogBundle? bundle) => _bundles.TryGetValue(agentId, out bundle);

    /// <summary>
    /// Evicts entries older than <see cref="Ttl" />, then — if still over <see cref="MaxEntries" /> —
    /// drops the oldest by received time until back under the cap. Idempotent; safe to call from a
    /// timer while uploads race in.
    /// </summary>
    public void Sweep(DateTimeOffset now)
    {
        foreach (KeyValuePair<Guid, AgentLogBundle> entry in _bundles)
        {
            if (now - entry.Value.ReceivedAt > Ttl)
            {
                _bundles.TryRemove(entry.Key, out _);
            }
        }

        int overflow = _bundles.Count - MaxEntries;
        if (overflow <= 0)
        {
            return;
        }

        foreach (Guid agentId in _bundles
            .OrderBy(kv => kv.Value.ReceivedAt)
            .Take(overflow)
            .Select(kv => kv.Key)
            .ToList())
        {
            _bundles.TryRemove(agentId, out _);
        }
    }
}