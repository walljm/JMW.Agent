using JMW.Discovery.Agent.Collection.Device.OnHub.Proto;

namespace JMW.Discovery.UnitTests.Agent.OnHub;

/// <summary>
/// Sanity checks that the report round-trips through the generated protobuf and
/// that the fields we consume (9/16/21) survive. The parse itself is Google.Protobuf.
/// </summary>
public sealed class DiagnosticReportTests
{
    [Fact]
    public void Parse_RoundTripsConsumedFields()
    {
        byte[] bytes = OnHubTestData.BuildReport(
            "ADEC2AD4",
            "state_seq_no: \"9784\"",
            ("cmd-a", "output-a"),
            (OnHubTestData.ApShowCommand, "station_info { }")
        );

        DiagnosticReport report = DiagnosticReport.Parser.ParseFrom(bytes);

        Assert.Equal("ADEC2AD4", report.DeviceId);
        Assert.Equal("state_seq_no: \"9784\"", report.NetworkState);
        Assert.Equal(2, report.CommandOutputs.Count);
        Assert.Equal("cmd-a", report.CommandOutputs[0].Command);
        Assert.Equal("output-a", report.CommandOutputs[0].Output);
        Assert.Equal(OnHubTestData.ApShowCommand, report.CommandOutputs[1].Command);
    }

    [Fact]
    public void Parse_MissingOptionalFields_DefaultToEmpty()
    {
        byte[] bytes = OnHubTestData.BuildReport("DEAD", networkState: "");

        DiagnosticReport report = DiagnosticReport.Parser.ParseFrom(bytes);

        Assert.Equal("DEAD", report.DeviceId);
        Assert.Equal(string.Empty, report.NetworkState);
        Assert.Empty(report.CommandOutputs);
    }

    [Fact]
    public void Parse_EmptyInput_YieldsDefaults()
    {
        DiagnosticReport report = DiagnosticReport.Parser.ParseFrom(Array.Empty<byte>());

        Assert.Equal(string.Empty, report.DeviceId);
        Assert.Equal(string.Empty, report.NetworkState);
        Assert.Empty(report.CommandOutputs);
    }
}