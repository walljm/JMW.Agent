using JMW.Discovery.Agent;

namespace JMW.Discovery.Tests;

/// <summary>
/// The in-process ring buffer is the on-demand log viewer's fallback capture source on every
/// non-systemd host. Its bounds (line count AND byte budget) and its Seq-based paging are the
/// load-bearing logic — a bug here either loses recent lines or breaks "load older".
/// </summary>
public sealed class LogRingBufferTests
{
    [Fact]
    public void Snapshot_ReturnsNewestFirst_WithMonotonicSeq()
    {
        LogRingBuffer buf = new(maxLines: 100, maxBytes: 1_000_000);
        buf.Append("one");
        buf.Append("two");
        buf.Append("three");

        IReadOnlyList<(long Seq, string Line)> page = buf.Snapshot(before: null, max: 10);

        Assert.Equal(3, page.Count);
        Assert.Equal("three", page[0].Line);
        Assert.Equal("two", page[1].Line);
        Assert.Equal("one", page[2].Line);
        // Seq strictly decreases going down the newest-first page.
        Assert.True(page[0].Seq > page[1].Seq && page[1].Seq > page[2].Seq);
    }

    [Fact]
    public void Append_EvictsOldest_WhenLineCapExceeded()
    {
        LogRingBuffer buf = new(maxLines: 3, maxBytes: 1_000_000);
        for (int i = 0; i < 6; i++)
        {
            buf.Append($"line{i}");
        }

        IReadOnlyList<(long Seq, string Line)> page = buf.Snapshot(before: null, max: 10);

        Assert.Equal(3, page.Count);
        Assert.Equal("line5", page[0].Line);
        Assert.Equal("line4", page[1].Line);
        Assert.Equal("line3", page[2].Line);
    }

    [Fact]
    public void Append_EvictsOldest_WhenByteCapExceeded()
    {
        // Each line is 10 bytes; a 25-byte budget holds at most 2 lines.
        LogRingBuffer buf = new(maxLines: 1000, maxBytes: 25);
        buf.Append("0123456789");
        buf.Append("abcdefghij");
        buf.Append("ABCDEFGHIJ");

        IReadOnlyList<(long Seq, string Line)> page = buf.Snapshot(before: null, max: 10);

        Assert.Equal(2, page.Count);
        Assert.Equal("ABCDEFGHIJ", page[0].Line);
        Assert.Equal("abcdefghij", page[1].Line);
    }

    [Fact]
    public void Append_KeepsAtLeastOneLine_EvenWhenSingleLineExceedsByteCap()
    {
        LogRingBuffer buf = new(maxLines: 1000, maxBytes: 4);
        buf.Append("this single line is far over the tiny byte budget");

        IReadOnlyList<(long Seq, string Line)> page = buf.Snapshot(before: null, max: 10);

        Assert.Single(page);
    }

    [Fact]
    public void Snapshot_Before_ReturnsOnlyOlderLines()
    {
        LogRingBuffer buf = new(maxLines: 100, maxBytes: 1_000_000);
        for (int i = 0; i < 5; i++)
        {
            buf.Append($"line{i}");
        }

        // Take the newest 2, then page older using the oldest Seq shown.
        IReadOnlyList<(long Seq, string Line)> first = buf.Snapshot(before: null, max: 2);
        long oldestShown = first[^1].Seq;

        IReadOnlyList<(long Seq, string Line)> older = buf.Snapshot(before: oldestShown, max: 2);

        Assert.All(older, e => Assert.True(e.Seq < oldestShown));
        Assert.Equal("line2", older[0].Line);
        Assert.Equal("line1", older[1].Line);
    }

    [Fact]
    public void Snapshot_RespectsMax()
    {
        LogRingBuffer buf = new(maxLines: 100, maxBytes: 1_000_000);
        for (int i = 0; i < 20; i++)
        {
            buf.Append($"line{i}");
        }

        Assert.Equal(5, buf.Snapshot(before: null, max: 5).Count);
    }

    [Fact]
    public void Snapshot_Empty_ReturnsEmpty()
    {
        LogRingBuffer buf = new();
        Assert.Empty(buf.Snapshot(before: null, max: 10));
    }
}