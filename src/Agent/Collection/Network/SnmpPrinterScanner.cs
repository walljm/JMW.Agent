using System.Net;

using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Probes each on-link neighbor with a unicast SNMP walk of the Printer-MIB (RFC 3805)
/// <c>prtGeneralSerialNumber</c>, yielding a vendor-neutral device serial for printers whose web UI
/// can't be reliably scraped (Lexmark, Xerox, Ricoh, Kyocera, Konica Minolta, Sharp). Uses SNMPv2c
/// community "public"; for other communities configure an explicit SNMP target so SnmpCollector runs
/// instead. Non-printers and SNMP-disabled hosts return null (walk times out or yields nothing).
/// Source tag: "snmp-printer". Disable via server config <c>Collectors["SnmpPrinterScanner"].Enabled</c>.
/// </summary>
public sealed class SnmpPrinterScanner : UnicastScannerBase
{
    // Printer-MIB prtGeneralSerialNumber column (table-indexed; walk it and take the first non-empty).
    private static readonly ObjectIdentifier PrtGeneralSerialNumber = new("1.3.6.1.2.1.43.5.1.1.17");

    // Short timeout: SNMP is UDP, so non-responders cost a full timeout — keep it small since most
    // neighbors are not printers.
    private const int TimeoutMs = 2000;

    public override string Name => "snmp-printer";

    protected override async Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out IPAddress? addr))
        {
            return null;
        }

        IPEndPoint endpoint = new(addr, 161);
        OctetString community = new("public");
        List<Variable> result = [];

        try
        {
            await Task.Run(
                () => Messenger.Walk(
                    VersionCode.V2,
                    endpoint,
                    community,
                    PrtGeneralSerialNumber,
                    result,
                    TimeoutMs,
                    WalkMode.WithinSubtree
                ),
                ct
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // no SNMP agent, not a printer, or timed out
        }

        string? serial = SelectFirstSerial(result);
        if (serial is null)
        {
            return null;
        }

        return new DiscoveredDevice
        {
            IpAddress = ip,
            Source = Name,
            Attributes = new Dictionary<string, string> { ["snmp.printer_serial"] = serial },
        };
    }

    /// <summary>Returns the first non-empty value from a prtGeneralSerialNumber walk, or null.</summary>
    public static string? SelectFirstSerial(IEnumerable<Variable> variables)
    {
        foreach (Variable v in variables)
        {
            string value = v.Data.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}