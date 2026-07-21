using System.Globalization;

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

    /// <summary>The display strings of a rendered row, dropping sort-value metadata.</summary>
    private static IReadOnlyList<string?> Disp(IReadOnlyList<FactCell> row) =>
        row.Select(c => c.Display).ToList();

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
        Assert.Equal(["coretemp/Core 0", "45"], Disp(view.Rows[0]));
        Assert.Equal(["coretemp/Core 1", "47"], Disp(view.Rows[1]));
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
        Assert.Equal(["openssl", "3.0.14", "security", "true"], Disp(Assert.Single(view.Rows)));
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

        Assert.Equal(["curl", null, null, null], Disp(Assert.Single(view.Rows)));
    }

    [Fact]
    public void UnrelatedFacts_AreIgnored_AndEmptyViewsOmitted()
    {
        // Facts that belong to no view produce no tables.
        List<FactViewFact> facts =
        [
            new("Device[].OS.Hostname", "", "web-01"),
            new("Device[].NoSuchThing[].Value", "eth0", "1000000000"),
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
        Assert.Equal(["Firewall", "true"], Disp(r.Rows[0]));
        Assert.Equal(["SELinux", "enforcing"], Disp(r.Rows[1]));
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
        Assert.Equal(["0", "8.8.8.8"], Disp(r.Rows[0]));
        Assert.Equal(["1", "1.1.1.1"], Disp(r.Rows[1]));
    }

    [Fact]
    public void BytesFormat_HumanizesDisplay_KeepsRawSortValue()
    {
        // A humanized byte column must sort on the raw count, not the "1.5 GB" text.
        FactViewDef view = new(
            "Disks",
            [
                FactViewColumn.Key("Disk"),
                FactViewColumn.Fact("Size", "Device[].Disk[].SizeBytes", FactViewFormat.Bytes),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Storage
        );
        List<FactViewFact> facts = [new("Device[].Disk[].SizeBytes", "sda", "1610612736")];

        RenderedFactView r = Assert.Single(FactViewRenderer.Render(facts, [view]));
        FactCell size = r.Rows[0][1];
        Assert.Equal("1.5 GB", size.Display);
        Assert.Equal("1610612736", size.SortValue);
    }

    [Fact]
    public void BytesFormat_NonNumericValue_FallsBackToRaw()
    {
        // Robustness: a non-numeric value must not throw — it passes through unformatted.
        FactViewDef view = new(
            "Disks",
            [
                FactViewColumn.Key("Disk"),
                FactViewColumn.Fact("Size", "Device[].Disk[].SizeBytes", FactViewFormat.Bytes),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Storage
        );
        List<FactViewFact> facts = [new("Device[].Disk[].SizeBytes", "sda", "unknown")];

        RenderedFactView r = Assert.Single(FactViewRenderer.Render(facts, [view]));
        FactCell size = r.Rows[0][1];
        Assert.Equal("unknown", size.Display);
        Assert.Null(size.SortValue);
    }

    [Fact]
    public void ComputedColumn_DerivesFromRowFacts_AtRenderTime()
    {
        // "Used %" is calculated from Used/Total facts in the same row, then Percent-formatted.
        const string used = "Device[].Filesystem[].UsedBytes";
        const string total = "Device[].Filesystem[].TotalBytes";
        FactViewDef view = new(
            "Filesystems",
            [
                FactViewColumn.Key("Mount"),
                FactViewColumn.Fact("Used", used, FactViewFormat.Bytes),
                FactViewColumn.Fact("Total", total, FactViewFormat.Bytes),
                FactViewColumn.Computed(
                    "Used %",
                    row => double.TryParse(row.GetValueOrDefault(used), NumberStyles.Float, CultureInfo.InvariantCulture, out double u)
                        && double.TryParse(row.GetValueOrDefault(total), NumberStyles.Float, CultureInfo.InvariantCulture, out double t)
                        && t > 0
                            ? (u / t * 100).ToString("0.#", CultureInfo.InvariantCulture)
                            : null,
                    FactViewFormat.Percent
                ),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Storage
        );
        List<FactViewFact> facts =
        [
            new(used, "/", "50"),
            new(total, "/", "200"),
        ];

        RenderedFactView r = Assert.Single(FactViewRenderer.Render(facts, [view]));
        // columns: Mount, Used, Total, Used %
        FactCell pct = r.Rows[0][3];
        Assert.Equal("25%", pct.Display);
        Assert.Equal("25", pct.SortValue);
    }

    [Fact]
    public void ComputedColumn_WithUndeclaredHiddenDependency_ReadsAsAbsent()
    {
        // A Computed column reading a fact that NO Fact column in the view also selects, and
        // that isn't listed in DependsOn, must see it as absent (null) — never throw, and never
        // silently see stale/unrelated data. This is the failure mode DependsOn exists to avoid:
        // without declaring it, the renderer never pulls MacAddress into the row at all.
        const string macAddress = "Device[].Interface[].MacAddress";
        FactViewDef view = new(
            "Interfaces",
            [
                FactViewColumn.Key("Interface"),
                FactViewColumn.Computed("Has MAC?", row => row.ContainsKey(macAddress) ? "yes" : "no"),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Network
        );
        List<FactViewFact> facts = [new(macAddress, "eth0", "aabbccddeeff")];

        // No Fact column anywhere selects macAddress, and no sample/dimKey exists for this view
        // at all (every column is a Key or an undeclared-dependency Computed column) — so the
        // view can't even resolve which dimension to group on, and renders nothing.
        Assert.Empty(FactViewRenderer.Render(facts, [view]));
    }

    [Fact]
    public void ComputedColumn_DependsOn_PullsInFactNoSiblingColumnSelects()
    {
        // The fix for the case above: declaring DependsOn makes the renderer select the fact into
        // the row even though no visible Fact column in the view also asks for it — mirrors the
        // "Interfaces" view's MAC/OUI/MAC Type columns, none of which have a raw MacAddress column.
        const string macAddress = "Device[].Interface[].MacAddress";
        FactViewDef view = new(
            "Interfaces",
            [
                FactViewColumn.Key("Interface"),
                FactViewColumn.Computed(
                    "Has MAC?",
                    row => row.ContainsKey(macAddress) ? "yes" : "no",
                    dependsOn: [macAddress]
                ),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Network
        );
        List<FactViewFact> facts = [new(macAddress, "eth0", "aabbccddeeff")];

        RenderedFactView r = Assert.Single(FactViewRenderer.Render(facts, [view]));
        Assert.Equal("yes", r.Rows[0][1].Display);
    }

    [Fact]
    public void PropertiesView_ComputedColumn_DependsOn_PullsInFactNoSiblingColumnSelects()
    {
        // Same DependsOn behavior on a Properties sheet (RenderProperties), not just a List view.
        const string a = "Device[].Security.FirewallEnabled";
        const string b = "Device[].Security.AvEnabled";
        FactViewDef view = new(
            "Security",
            [
                FactViewColumn.Computed(
                    "Both enabled?",
                    row => row.GetValueOrDefault(a) == "true" && row.GetValueOrDefault(b) == "true" ? "yes" : "no",
                    dependsOn: [a, b]
                ),
            ],
            Kind: FactViewKind.Properties,
            Group: FactViewGroup.Security
        );
        List<FactViewFact> facts = [new(a, "", "true"), new(b, "", "true")];

        RenderedFactView r = Assert.Single(FactViewRenderer.Render(facts, [view]));
        Assert.Equal("yes", r.Rows[0][1].Display);
    }

    [Fact]
    public void OuiFormat_ResolvesViaRenderContext_AndFallsBackWhenAbsent()
    {
        const string mac = "Device[].Interface[].MacAddress";
        FactViewDef view = new(
            "Interfaces",
            [
                FactViewColumn.Key("Interface"),
                FactViewColumn.Fact("OUI", mac, FactViewFormat.Oui),
            ],
            Kind: FactViewKind.List,
            Group: FactViewGroup.Network
        );
        List<FactViewFact> facts = [new(mac, "eth0", "00:11:22:33:44:55")];

        // With an injected resolver → vendor (country).
        FactViewRenderContext ctx = new(_ => ("Cisco", "US"));
        RenderedFactView resolved = Assert.Single(FactViewRenderer.Render(facts, [view], ctx));
        Assert.Equal("Cisco (US)", resolved.Rows[0][1].Display);

        // Without one (Empty context / no arg) → unknown dash, never throws.
        RenderedFactView bare = Assert.Single(FactViewRenderer.Render(facts, [view]));
        Assert.Equal("—", bare.Rows[0][1].Display);
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