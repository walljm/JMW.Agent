using JMW.Discovery.Server.Reporting;

namespace JMW.Discovery.Tests;

/// <summary>
/// Tests the keyset pagination cursor format used by AgentsApi and the Agents Razor Page:
/// (CreatedAt ISO-8601, AgentId) encoded via <see cref="KeysetCursor.EncodeParts" /> (review D30 —
/// AgentsApi previously hand-rolled its own base64(ts + "|" + id) cursor instead of using the
/// shared primitive; <see cref="KeysetCursor" /> itself is covered by
/// <c>ReportingHelpersTests</c>, so this only exercises AgentsApi's specific 2-part usage).
/// </summary>
public sealed class AgentKeysetCursorTests
{
    [Fact]
    public void Cursor_RoundTrips()
    {
        DateTimeOffset createdAt = new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);
        Guid agentId = Guid.NewGuid();

        string encoded = KeysetCursor.EncodeParts(createdAt.ToString("O"), agentId.ToString());

        Assert.True(KeysetCursor.TryDecodeParts(encoded, 2, out string[] parts));
        Assert.True(DateTimeOffset.TryParse(parts[0], out DateTimeOffset decodedTs));
        Assert.True(Guid.TryParse(parts[1], out Guid decodedId));
        Assert.Equal(createdAt, decodedTs);
        Assert.Equal(agentId, decodedId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("notbase64!!!")]
    public void DecodeCursor_ReturnsFalseForInvalidInput(string cursor)
    {
        Assert.False(KeysetCursor.TryDecodeParts(cursor, 2, out _));
    }

    [Fact]
    public void DecodeCursor_ReturnsFalseWhenAgentIdPartIsNotAGuid()
    {
        string cursor = KeysetCursor.EncodeParts(DateTimeOffset.UtcNow.ToString("O"), "not-a-guid");

        Assert.True(KeysetCursor.TryDecodeParts(cursor, 2, out string[] parts));
        Assert.False(Guid.TryParse(parts[1], out _));
    }
}