using JMW.Discovery.Agent.Collection.Device.EnterpriseNumbers;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// <see cref="EnterpriseNumberRegistry" /> loads the embedded IANA Private Enterprise Numbers
/// TSV snapshot (see ATTRIBUTION.md) into an in-memory lookup once. These tests exercise the
/// actual embedded resource, not a fake — a broken embed (wrong glob, bad TSV) would otherwise
/// only surface at runtime against a real SNMP device.
/// </summary>
public sealed class EnterpriseNumberRegistryTests
{
    [Fact]
    public void Lookup_KnownNumber_ReturnsRegistrantName()
    {
        Assert.Equal("ciscoSystems", EnterpriseNumberRegistry.Lookup(9));
        Assert.Equal("MikroTik", EnterpriseNumberRegistry.Lookup(14988));
    }

    [Fact]
    public void Lookup_UnassignedNumber_ReturnsNull()
    {
        Assert.Null(EnterpriseNumberRegistry.Lookup(99999));
    }

    [Fact]
    public void Lookup_ReservedNumberZero_ReturnsNull()
    {
        // Enterprise 0 is literally "Reserved" in the registry — excluded from the embedded
        // snapshot on purpose (see ATTRIBUTION.md), so it must not resolve to anything.
        Assert.Null(EnterpriseNumberRegistry.Lookup(0));
    }

    [Fact]
    public void Registry_HasTensOfThousandsOfEntries()
    {
        // Sanity check that the embedded resource actually loaded the full registry, not an
        // empty/truncated file — the real IANA registry has 60k+ assigned (non-reserved) numbers.
        int hits = 0;
        for (int i = 1; i < 70000; i++)
        {
            if (EnterpriseNumberRegistry.Lookup(i) is not null)
            {
                hits++;
            }
        }

        Assert.True(hits > 60000, $"Expected 60000+ resolvable enterprise numbers under 70000, found {hits}.");
    }
}
