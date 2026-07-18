using JMW.Discovery.Agent.Collection.Local;
using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Covers parsing of <c>smartctl -j -a</c> JSON in <see cref="DiskCollector.ParseLinuxSmart"/>.
/// The focus is the NVMe wear-mapping bug: NVMe endurance lives in
/// <c>nvme_smart_health_information_log.percentage_used</c>, but the Disks report reads only
/// <see cref="FactPaths.DiskSmartWearPercent"/> (populated for SATA/SSD from ATA attributes
/// 231/233). NVMe drives therefore showed a blank WEAR % column even with valid endurance data,
/// including brand-new drives that legitimately report <c>percentage_used: 0</c>.
/// </summary>
public sealed class DiskCollectorSmartTests
{
    private static readonly string[] Keys = ["dev0", "sda"];

    private static double? Wear(List<Fact> facts) =>
        facts.FirstOrDefault(f => f.AttributePath == FactPaths.DiskSmartWearPercent).Value.AsDouble();

    private static double? PercentageUsed(List<Fact> facts) =>
        facts.FirstOrDefault(f => f.AttributePath == FactPaths.DiskSmartPercentageUsed).Value.AsDouble();

    private static bool Has(List<Fact> facts, string path) =>
        facts.Any(f => f.AttributePath == path);

    // ── NVMe: percentage_used maps to WearPercent, including the new-drive (0) case ──

    [Theory]
    [InlineData(0.0)] // brand-new drive — the regression case; must still emit wear = 0
    [InlineData(1.0)]
    [InlineData(37.0)]
    [InlineData(100.0)] // fully spent — boundary, still valid
    public void NvmePercentageUsed_MapsToWearPercent(double used)
    {
        string json = NvmeJson(percentageUsed: used);
        List<Fact> facts = [];

        DiskCollector.ParseLinuxSmart(json, Keys, facts);

        Assert.Equal(used, Wear(facts));
        // Native NVMe endurance fact is preserved alongside the wear mapping.
        Assert.Equal(used, PercentageUsed(facts));
    }

    [Theory]
    [InlineData(101.0)] // over-endurance: past rated life (NVMe spec allows up to 255)
    [InlineData(150.0)]
    public void NvmePercentageUsed_OutOfWearRange_KeepsEnduranceButOmitsWear(double used)
    {
        string json = NvmeJson(percentageUsed: used);
        List<Fact> facts = [];

        DiskCollector.ParseLinuxSmart(json, Keys, facts);

        // WearPercent is a 0..100 column; the raw endurance is still surfaced separately.
        Assert.Null(Wear(facts));
        Assert.Equal(used, PercentageUsed(facts));
    }

    [Fact]
    public void Nvme_EmitsSharedHealthFields()
    {
        string json = NvmeJson(percentageUsed: 5);
        List<Fact> facts = [];

        DiskCollector.ParseLinuxSmart(json, Keys, facts);

        Assert.Equal(
            "PASSED",
            facts.First(f => f.AttributePath == FactPaths.DiskSmartOverallHealth).Value.AsString()
        );
        Assert.Equal(34, facts.First(f => f.AttributePath == FactPaths.DiskSmartTempC).Value.AsDouble());
        Assert.Equal(89, facts.First(f => f.AttributePath == FactPaths.DiskSmartPowerOnHours).Value.AsLong());
    }

    // ── SATA/SSD: wear still comes from ATA attributes 231/233 (100 - normalized value) ──

    [Theory]
    [InlineData(233, 90, 10.0)] // SSD Life Left normalized 90 → 10% worn
    [InlineData(231, 100, 0.0)] // new SSD, normalized 100 → 0% worn (must be emitted)
    [InlineData(231, 5, 95.0)]
    public void AtaWearAttribute_MapsToWearPercent(int id, int normalizedValue, double expectedWear)
    {
        string json = AtaJson(wearId: id, wearValue: normalizedValue);
        List<Fact> facts = [];

        DiskCollector.ParseLinuxSmart(json, Keys, facts);

        Assert.Equal(expectedWear, Wear(facts));
    }

    [Fact]
    public void AtaWearValueOfZero_IsTreatedAsUnreported()
    {
        // ATA normalized 0 usually means "attribute not supported"; wear would be a bogus 100.
        string json = AtaJson(wearId: 231, wearValue: 0);
        List<Fact> facts = [];

        DiskCollector.ParseLinuxSmart(json, Keys, facts);

        Assert.Null(Wear(facts));
    }

    [Fact]
    public void SataDrive_WithoutWearAttribute_EmitsNoWear()
    {
        // A generic SSD that reports no 231/233 attribute (e.g. core.home's "M.2 SSD 512GB").
        string json = """
            {
              "smart_status": { "passed": true },
              "temperature": { "current": 40 },
              "power_on_time": { "hours": 1961 },
              "ata_smart_attributes": {
                "table": [
                  { "id": 5, "raw": { "value": 0 } },
                  { "id": 199, "raw": { "value": 0 } }
                ]
              }
            }
            """;
        List<Fact> facts = [];

        DiskCollector.ParseLinuxSmart(json, Keys, facts);

        Assert.Null(Wear(facts));
        // ...but the other SMART columns still populate.
        Assert.True(Has(facts, FactPaths.DiskSmartOverallHealth));
        Assert.True(Has(facts, FactPaths.DiskSmartPowerOnHours));
    }

    // ── Malformed / empty input must never throw and must emit nothing ──

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"smart_status\": ")] // truncated
    [InlineData("{}")]
    public void MalformedOrEmptyInput_ProducesNoFactsAndDoesNotThrow(string json)
    {
        List<Fact> facts = [];

        DiskCollector.ParseLinuxSmart(json, Keys, facts);

        Assert.Empty(facts);
    }

    private static string NvmeJson(double percentageUsed) =>
        $$"""
        {
          "model_name": "CT1000P310SSD8",
          "smart_status": { "passed": true },
          "temperature": { "current": 34 },
          "power_on_time": { "hours": 89 },
          "nvme_smart_health_information_log": {
            "percentage_used": {{percentageUsed}},
            "available_spare": 100,
            "data_units_read": 1000,
            "data_units_written": 2000
          }
        }
        """;

    private static string AtaJson(int wearId, int wearValue) =>
        $$"""
        {
          "smart_status": { "passed": true },
          "temperature": { "current": 39 },
          "power_on_time": { "hours": 40251 },
          "ata_smart_attributes": {
            "table": [
              { "id": 5, "raw": { "value": 0 } },
              { "id": {{wearId}}, "value": {{wearValue}}, "raw": { "value": 0 } }
            ]
          }
        }
        """;
}