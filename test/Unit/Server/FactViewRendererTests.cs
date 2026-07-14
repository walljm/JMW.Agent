using JMW.Discovery.Core.Analysis;
using JMW.Discovery.Server.FactViews;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// Unit tests for FactViewRenderer — the pure fact-list → display-table transform that
/// backs the device-detail fact views (no projection, no DB).
/// </summary>
public sealed class FactViewRendererTests
{
    // Templated attribute paths as they arrive from the All-Facts query (empty brackets).
    private const string ThermalCelsius = "Device[].Hardware.Temperature[].Celsius";
    private const string PendingName = "Device[].Updates.Pending[].Name";
    private const string PendingVersion = "Device[].Updates.Pending[].NewVersion";
    private const string PendingSource = "Device[].Updates.Pending[].Source";
    private const string PendingSecurity = "Device[].Updates.Pending[].Security";

    [Fact]
    public void KeyColumn_UsesRowIdentity_AndPivotsAttribute()
    {
        // Two thermal zones → two rows; Zone column = the dimension key, °C = the value.
        List<FactViewFact> facts =
        [
            new(ThermalCelsius, "coretemp/Core 0", "45"),
            new(ThermalCelsius, "coretemp/Core 1", "47"),
        ];

        RenderedFactView view = Assert.Single(
            FactViewRenderer.Render(facts, FactViewLibrary.All),
            v => v.Title == "Thermal"
        );

        Assert.Equal(["Zone", "°C"], view.Headers);
        Assert.Equal(2, view.Rows.Count);
        Assert.Equal(["coretemp/Core 0", "45"], view.Rows[0]);
        Assert.Equal(["coretemp/Core 1", "47"], view.Rows[1]);
    }

    [Fact]
    public void MultipleAttributes_PivotIntoOneRowPerKey()
    {
        // Four facts for one package → one row with four columns.
        List<FactViewFact> facts =
        [
            new(PendingName, "openssl", "openssl"),
            new(PendingVersion, "openssl", "3.0.14"),
            new(PendingSource, "openssl", "security"),
            new(PendingSecurity, "openssl", "true"),
        ];

        RenderedFactView view = Assert.Single(
            FactViewRenderer.Render(facts, FactViewLibrary.All),
            v => v.Title == "Pending Updates"
        );

        Assert.Equal(["Package", "New Version", "Source", "Security"], view.Headers);
        List<string?> row = Assert.Single(view.Rows).ToList();
        Assert.Equal(["openssl", "3.0.14", "security", "true"], row);
    }

    [Fact]
    public void MissingAttribute_LeavesCellNull()
    {
        // A package with only a name → other columns are null, not dropped.
        List<FactViewFact> facts = [new(PendingName, "curl", "curl")];

        RenderedFactView view = Assert.Single(
            FactViewRenderer.Render(facts, FactViewLibrary.All),
            v => v.Title == "Pending Updates"
        );

        Assert.Equal(["curl", null, null, null], Assert.Single(view.Rows));
    }

    [Fact]
    public void UnrelatedFacts_AreIgnored_AndEmptyViewsOmitted()
    {
        // Facts that belong to no view produce no tables.
        List<FactViewFact> facts =
        [
            new("Device[].OS.Hostname", "", "web-01"),
            new("Device[].Interface[].SpeedBps", "eth0", "1000000000"),
        ];

        Assert.Empty(FactViewRenderer.Render(facts, FactViewLibrary.All));
    }

    [Fact]
    public void PropertiesView_RendersScalarFactsAsPropertyValueRows()
    {
        // A scalar property sheet: each column → a (Property, Value) row; absent facts omitted.
        FactViewDef view = new(
            "Security",
            [
                FactViewColumn.Fact("Firewall", "Device[].Security.FirewallEnabled"),
                FactViewColumn.Fact("SELinux", "Device[].Security.SELinuxMode"),
                FactViewColumn.Fact("TPM", "Device[].Security.TpmPresent"),
            ],
            Kind: FactViewKind.Properties,
            Group: FactViewGroup.Security
        );
        List<FactViewFact> facts =
        [
            new("Device[].Security.FirewallEnabled", "", "true"),
            new("Device[].Security.SELinuxMode", "", "enforcing"),
            // TPM absent → its row is omitted
        ];

        RenderedFactView r = Assert.Single(FactViewRenderer.Render(facts, [view]));
        Assert.Equal(["Property", "Value"], r.Headers);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(["Firewall", "true"], r.Rows[0]);
        Assert.Equal(["SELinux", "enforcing"], r.Rows[1]);
    }

    [Fact]
    public void ListView_ValueIsTheRow_WhenAttributeIsEmpty()
    {
        // Network.DNS[] — the fact value IS the datum (no sub-attribute after the list).
        FactViewDef view = new(
            "DNS Servers",
            [FactViewColumn.Key("#"), FactViewColumn.Fact("Server", "Device[].Network.DNS[]")],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Protocols
        );
        List<FactViewFact> facts =
        [
            new("Device[].Network.DNS[]", "0", "8.8.8.8"),
            new("Device[].Network.DNS[]", "1", "1.1.1.1"),
        ];

        RenderedFactView r = Assert.Single(FactViewRenderer.Render(facts, [view]));
        Assert.Equal(["#", "Server"], r.Headers);
        Assert.Equal(["0", "8.8.8.8"], r.Rows[0]);
        Assert.Equal(["1", "1.1.1.1"], r.Rows[1]);
    }

    [Fact]
    public void ColumnFactPaths_MatchRealConstants()
    {
        // Guards the library against a stale hand-typed path: the view columns must reference
        // the same FactPaths the collectors emit.
        Assert.Contains(FactPaths.HwTemperatureCelsius, FactViewLibrary.AllConsumedFactPaths());
        Assert.Contains(FactPaths.UpdatePendingName, FactViewLibrary.AllConsumedFactPaths());
        Assert.Contains(FactPaths.UpdatePendingSecurity, FactViewLibrary.AllConsumedFactPaths());
    }
}