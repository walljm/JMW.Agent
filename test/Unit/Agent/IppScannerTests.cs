using System.Text;

using JMW.Discovery.Agent.Collection.Network;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Tests IPP attribute extraction, including the newly-pulled vendor-neutral
/// <c>printer-firmware-string-version</c> (PWG 5110.1). Builds IPP-encoded attributes
/// (name-length + name + value-length + value) the way a real Get-Printer-Attributes response does.
/// </summary>
public sealed class IppScannerTests
{
    private static byte[] Attr(string name, string value)
    {
        List<byte> b =
        [
            0x41, // value tag (textWithoutLanguage) — ignored by the extractor, present for realism
            (byte)(name.Length >> 8), (byte)(name.Length & 0xFF),
            .. Encoding.ASCII.GetBytes(name),
            (byte)(value.Length >> 8), (byte)(value.Length & 0xFF),
            .. Encoding.ASCII.GetBytes(value),
        ];
        return [.. b];
    }

    [Fact]
    public void ExtractIppAttribute_PullsModelNameAndFirmware()
    {
        byte[] response =
        [
            .. new byte[8], // IPP header (version/status/request-id) — skipped by the scanner
            .. Attr("printer-make-and-model", "HP LaserJet 400 MFP M425dw"),
            .. Attr("printer-name", "NPI8G353W"),
            .. Attr("printer-firmware-string-version", "20130415"),
        ];

        Assert.Equal("HP LaserJet 400 MFP M425dw", IppScanner.ExtractIppAttribute(response, "printer-make-and-model"));
        Assert.Equal("NPI8G353W", IppScanner.ExtractIppAttribute(response, "printer-name"));
        Assert.Equal("20130415", IppScanner.ExtractIppAttribute(response, "printer-firmware-string-version"));
    }

    [Fact]
    public void ExtractIppAttribute_ReturnsNullWhenAbsent()
    {
        byte[] response = [.. new byte[8], .. Attr("printer-name", "LJ")];
        Assert.Null(IppScanner.ExtractIppAttribute(response, "printer-firmware-string-version"));
    }
}