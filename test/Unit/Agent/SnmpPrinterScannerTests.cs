using JMW.Discovery.Agent.Collection.Network;

using Lextm.SharpSnmpLib;

namespace JMW.Discovery.UnitTests.Agent;

/// <summary>
/// Tests the serial-selection logic of <see cref="SnmpPrinterScanner"/> — a walk of Printer-MIB
/// prtGeneralSerialNumber returns the first non-empty value (SOHO devices often answer the OID with
/// an empty string).
/// </summary>
public sealed class SnmpPrinterScannerTests
{
    private static Variable Serial(string index, string value) =>
        new(new ObjectIdentifier($"1.3.6.1.2.1.43.5.1.1.17.{index}"), new OctetString(value));

    [Fact]
    public void SelectFirstSerial_ReturnsFirstNonEmpty()
    {
        Variable[] walk = [Serial("1", "   "), Serial("2", "CNF8G353WR")];
        Assert.Equal("CNF8G353WR", SnmpPrinterScanner.SelectFirstSerial(walk));
    }

    [Fact]
    public void SelectFirstSerial_TrimsWhitespace()
    {
        Assert.Equal("ABC123", SnmpPrinterScanner.SelectFirstSerial([Serial("1", "  ABC123 ")]));
    }

    [Fact]
    public void SelectFirstSerial_ReturnsNullWhenAllEmpty()
    {
        Assert.Null(SnmpPrinterScanner.SelectFirstSerial([Serial("1", ""), Serial("2", "   ")]));
    }

    [Fact]
    public void SelectFirstSerial_ReturnsNullForEmptyWalk()
    {
        Assert.Null(SnmpPrinterScanner.SelectFirstSerial([]));
    }
}