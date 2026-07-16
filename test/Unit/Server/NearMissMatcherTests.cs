using JMW.Discovery.Server.ManualFacts;

namespace JMW.Discovery.UnitTests.Server;

/// <summary>
/// Advisory near-miss detection (REQ-003, architecture §6): warn iff exactly one catalog template is
/// a close match. A missing "[]" (the common dimensionality typo) normalizes to distance 0.
/// </summary>
public sealed class NearMissMatcherTests
{
    private static readonly string[] Candidates =
    [
        "Device[].Interface[].SpeedBps",
        "Device[].Interface[].Mtu",
        "Device[].Disk[].SerialNumber",
    ];

    [Fact]
    public void MissingBrackets_SuggestsTheRealCatalogPath()
    {
        // "Device[].InterfaceSpeedBps" and "Device[].Interface[].SpeedBps" both normalize to
        // "interfacespeedbps" — distance 0, the single suggestion.
        Assert.Equal(
            "Device[].Interface[].SpeedBps",
            NearMissMatcher.FindSuggestion("Device[].InterfaceSpeedBps", Candidates)
        );
    }

    [Fact]
    public void CaseAndPunctuationDifferences_StillMatch() =>
        Assert.Equal(
            "Device[].Disk[].SerialNumber",
            NearMissMatcher.FindSuggestion("Device[].disk[].serial_number", Candidates)
        );

    [Fact]
    public void NoCloseCandidate_ReturnsNull() =>
        Assert.Null(NearMissMatcher.FindSuggestion("Device[].SwitchPortUplinkLabel", Candidates));

    [Fact]
    public void MoreThanOneCandidateWithinThreshold_ReturnsNull()
    {
        // "interfacespeed" is within edit distance of both Interface[].SpeedBps ("interfacespeedbps")
        // and, with a small threshold, ambiguous enough that a single winner is not asserted.
        string[] ambiguous = ["Device[].Interface[].Speed", "Device[].Interface[].Speeds"];
        Assert.Null(NearMissMatcher.FindSuggestion("Device[].Interface[].Speedd", ambiguous));
    }

    [Fact]
    public void EmptyInput_ReturnsNull() =>
        Assert.Null(NearMissMatcher.FindSuggestion("Device[].", Candidates));
}
