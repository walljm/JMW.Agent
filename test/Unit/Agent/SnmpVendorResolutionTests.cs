using System.Reflection;

using JMW.Discovery.Agent.Collection.Device;
using JMW.Discovery.Agent.Collection.Device.EnterpriseNumbers;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// <see cref="SnmpCollector" />'s private ResolveVendor extracts the IANA enterprise number from
/// a sysObjectID (<c>1.3.6.1.4.1.&lt;number&gt;...</c>) and looks it up against the full embedded
/// IANA registry (<see cref="EnterpriseNumberRegistry" />) — see
/// docs/plans/vendor-derivation-updates.md §2.5. ResolveVendor returns IANA's raw registrant
/// string unmodified; VendorNormalizer canonicalizes it downstream (see NormalizerTests.cs for
/// the specific alias mappings this raw data needed).
/// </summary>
public sealed class SnmpVendorResolutionTests
{
    private static string? Resolve(string sysObjectId)
    {
        MethodInfo m = typeof(SnmpCollector).GetMethod(
                "ResolveVendor",
                BindingFlags.NonPublic | BindingFlags.Static
            )
         ?? throw new InvalidOperationException("SnmpCollector.ResolveVendor not found.");
        return (string?)m.Invoke(null, [sysObjectId]);
    }

    // Raw IANA registrant names — intentionally uncleaned (see class remarks). These are the
    // exact strings the live registry returns for these numbers as of the 2026-07-14 snapshot.
    [Theory]
    [InlineData("1.3.6.1.4.1.9.1.516", "ciscoSystems")]
    [InlineData("1.3.6.1.4.1.11.2.3.7.1", "Hewlett-Packard")]
    [InlineData("1.3.6.1.4.1.4526.100.2.3", "Netgear")]
    [InlineData("1.3.6.1.4.1.318.1.3.2.1", "American Power Conversion Corp.")]
    [InlineData("1.3.6.1.4.1.14988.1.1.1", "MikroTik")]
    [InlineData("1.3.6.1.4.1.41112.1.6", "Ubiquiti Networks, Inc.")]
    [InlineData("1.3.6.1.4.1.6574.1.1", "Synology Inc.")]
    [InlineData("1.3.6.1.4.1.24681.1.2.16", "QNAP SYSTEMS, INC")]
    [InlineData("1.3.6.1.4.1.2636.1.1.1.2.30", "Juniper Networks, Inc.")]
    [InlineData("1.3.6.1.4.1.11863.1.1", "TP-Link Systems Inc.")]
    [InlineData("1.3.6.1.4.1.171.10.36.1", "D-Link Systems, Inc.")]
    [InlineData("1.3.6.1.4.1.2623.1.1", "ASUSTek Computer Inc.")]
    [InlineData("1.3.6.1.4.1.12356.101.1.20001", "Fortinet, Inc.")]
    [InlineData("1.3.6.1.4.1.25461.2.3.1", "PALO ALTO NETWORKS")]
    [InlineData("1.3.6.1.4.1.14823.1.1.4", "Aruba, a Hewlett Packard Enterprise company")]
    public void ResolveVendor_KnownEnterpriseNumber_ReturnsRawRegistrantName(string sysObjectId, string vendor)
    {
        Assert.Equal(vendor, Resolve(sysObjectId));
    }

    [Theory]
    [InlineData("1.3.6.1.4.1.9")] // bare enterprise number, no trailing product path
    [InlineData("1.3.6.1.4.1.14988")]
    public void ResolveVendor_BareEnterpriseNumber_StillMatches(string sysObjectId)
    {
        Assert.NotNull(Resolve(sysObjectId));
    }

    [Fact]
    public void ResolveVendor_TpLinkNotMisclassifiedAsHp()
    {
        // TP-Link's enterprise number (11863) shares leading digits with HP's (11). Extracting
        // the enterprise number as the exact next dot-segment (not a string-prefix check) must
        // treat these as distinct numbers.
        Assert.Equal("TP-Link Systems Inc.", Resolve("1.3.6.1.4.1.11863.1.1"));
    }

    [Theory]
    [InlineData("1.3.6.1.4.1.99999.1.1")] // unassigned enterprise number
    [InlineData("1.3.6.1.4.1.0.1.1")] // Reserved — excluded from the embedded registry on purpose
    [InlineData("1.3.6.1.4.1.abc.1.1")] // non-numeric segment where the enterprise number should be
    [InlineData("1.2.3.4.5.6.9.1")] // wrong OID family entirely (not under 1.3.6.1.4.1)
    [InlineData("")]
    public void ResolveVendor_UnknownOrMalformed_ReturnsNull(string sysObjectId)
    {
        Assert.Null(Resolve(sysObjectId));
    }
}