using JMW.Discovery.Server.Infrastructure;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// The agent log cache holds uploaded log pages in memory only (never Postgres). Its eviction —
/// TTL by receive time, then a hard cap on cached agents — is what keeps a forgotten fleet or an
/// idle admin tab from growing it unbounded.
/// </summary>
public sealed class AgentLogCacheTests
{
    private static AgentLogBundle Bundle(DateTimeOffset receivedAt, string text = "log") =>
        new(RequestedAt: receivedAt, ReceivedAt: receivedAt, Source: "buffer", Truncated: false, Text: text, NextBeforeToken: null);

    [Fact]
    public void Set_Then_TryGet_ReturnsLatestBundle()
    {
        AgentLogCache cache = new();
        Guid agent = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        cache.Set(agent, Bundle(now, "first"));
        cache.Set(agent, Bundle(now, "second"));

        Assert.True(cache.TryGet(agent, out AgentLogBundle? got));
        Assert.NotNull(got);
        Assert.Equal("second", got!.Text);
    }

    [Fact]
    public void TryGet_Missing_ReturnsFalse()
    {
        AgentLogCache cache = new();
        Assert.False(cache.TryGet(Guid.NewGuid(), out AgentLogBundle? got));
        Assert.Null(got);
    }

    [Fact]
    public void Sweep_EvictsEntriesOlderThanTtl()
    {
        AgentLogCache cache = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid fresh = Guid.NewGuid();
        Guid stale = Guid.NewGuid();

        cache.Set(fresh, Bundle(now));
        cache.Set(stale, Bundle(now - AgentLogCache.Ttl - TimeSpan.FromMinutes(1)));

        cache.Sweep(now);

        Assert.True(cache.TryGet(fresh, out _));
        Assert.False(cache.TryGet(stale, out _));
    }

    [Fact]
    public void Sweep_CapsToMaxEntries_KeepingNewest()
    {
        AgentLogCache cache = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // One clearly-newest agent plus MaxEntries older ones — the oldest must be dropped.
        Guid newest = Guid.NewGuid();
        cache.Set(newest, Bundle(now));
        for (int i = 0; i < AgentLogCache.MaxEntries; i++)
        {
            cache.Set(Guid.NewGuid(), Bundle(now - TimeSpan.FromSeconds(i + 1)));
        }

        cache.Sweep(now);

        Assert.True(cache.TryGet(newest, out _));
    }
}