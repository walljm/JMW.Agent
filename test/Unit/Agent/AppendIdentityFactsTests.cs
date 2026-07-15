using System.Reflection;

using JMW.Discovery.Agent;
using JMW.Discovery.Agent.Collection;
using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// <see cref="global::JMW.Discovery.Agent.Agent" />'s private AppendIdentityFacts is the fix for a real
/// data-loss bug: a collector's DeviceIdentity (Kind/Vendor/OsFamily/OsVersion) previously flowed
/// only into CollectionContext.RegisterProbeAsync, which discarded everything except
/// Fingerprints — nothing downstream ever read Kind/Vendor/OsFamily/OsVersion past that point.
/// GoogleWifiCollector happened to also emit an explicit Device[].Vendor/Kind fact, masking the
/// bug for that one collector; Bacnet/Modbus/Ssh/Snmp set these on DeviceIdentity only, so their
/// values were silently dropped (e.g. every non-Google-Wifi device's Kind was NULL). This method
/// fills the gap by emitting the missing fact from identity — but only when the collector didn't
/// already emit one explicitly, so no double-write.
/// </summary>
public sealed class AppendIdentityFactsTests
{
    private static readonly DateTimeOffset T = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<Fact> Invoke(IReadOnlyList<Fact> facts, DeviceIdentity? identity, string? placeholder)
    {
        MethodInfo m = typeof(global::JMW.Discovery.Agent.Agent).GetMethod(
                "AppendIdentityFacts",
                BindingFlags.NonPublic | BindingFlags.Static
            )
         ?? throw new InvalidOperationException("Agent.AppendIdentityFacts not found.");
        return (IReadOnlyList<Fact>)m.Invoke(null, [facts, identity, placeholder])!;
    }

    [Fact]
    public void MissingKindAndVendor_BothAppended()
    {
        DeviceIdentity identity = new(
            Fingerprints: [],
            Kind: "network-device",
            Vendor: "Cisco",
            OsFamily: null,
            OsVersion: null
        );

        IReadOnlyList<Fact> result = Invoke([], identity, "_probe_");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, f => f.AttributePath == FactPaths.DeviceKind && f.Value.AsString() == "network-device");
        Assert.Contains(result, f => f.AttributePath == FactPaths.DeviceVendor && f.Value.AsString() == "Cisco");
    }

    [Fact]
    public void OsFamilyAndOsVersion_Appended()
    {
        DeviceIdentity identity = new(
            Fingerprints: [],
            Kind: null,
            Vendor: null,
            OsFamily: "linux",
            OsVersion: "5.15.0"
        );

        IReadOnlyList<Fact> result = Invoke([], identity, "_probe_");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, f => f.AttributePath == FactPaths.SystemOsFamily && f.Value.AsString() == "linux");
        Assert.Contains(result, f => f.AttributePath == FactPaths.SystemOsVersion && f.Value.AsString() == "5.15.0");
    }

    [Fact]
    public void CollectorAlreadyEmittedVendorExplicitly_NotDoubleWritten()
    {
        // Mirrors GoogleWifiCollector: sets Vendor on DeviceIdentity AND separately emits an
        // explicit Device[].Vendor fact. The explicit fact must win — no duplicate appended.
        Fact explicitVendor = Fact.Create(FactPaths.DeviceVendor, ["_probe_"], "Google", T);
        DeviceIdentity identity = new(
            Fingerprints: [],
            Kind: "router",
            Vendor: "google",
            OsFamily: null,
            OsVersion: null
        );

        IReadOnlyList<Fact> result = Invoke([explicitVendor], identity, "_probe_");

        Assert.Equal(2, result.Count); // the original explicit fact + the auto-added Kind
        Fact vendorFact = Assert.Single(result, f => f.AttributePath == FactPaths.DeviceVendor);
        Assert.Equal("Google", vendorFact.Value.AsString()); // explicit value preserved, not "google"
        Assert.Contains(result, f => f.AttributePath == FactPaths.DeviceKind);
    }

    [Fact]
    public void NullIdentity_FactsUnchanged()
    {
        Fact[] facts = [Fact.Create(FactPaths.SnmpSysName, ["_probe_"], "switch1", T)];
        IReadOnlyList<Fact> result = Invoke(facts, null, "_probe_");
        Assert.Same(facts, result);
    }

    [Fact]
    public void NullDevicePlaceholder_FactsUnchanged()
    {
        DeviceIdentity identity = new(Fingerprints: [], Kind: "host", Vendor: null, OsFamily: null, OsVersion: null);
        Fact[] facts = [];
        IReadOnlyList<Fact> result = Invoke(facts, identity, null);
        Assert.Same(facts, result);
    }

    [Fact]
    public void AllFieldsNull_NoAdditions()
    {
        DeviceIdentity identity = new(Fingerprints: [], Kind: null, Vendor: null, OsFamily: null, OsVersion: null);
        Fact[] facts = [Fact.Create(FactPaths.SnmpSysName, ["_probe_"], "switch1", T)];

        IReadOnlyList<Fact> result = Invoke(facts, identity, "_probe_");
        Assert.Same(facts, result);
    }

    [Fact]
    public void WhitespaceOnlyField_TreatedAsMissing()
    {
        DeviceIdentity identity = new(Fingerprints: [], Kind: "   ", Vendor: null, OsFamily: null, OsVersion: null);
        IReadOnlyList<Fact> result = Invoke([], identity, "_probe_");
        Assert.Empty(result);
    }
}