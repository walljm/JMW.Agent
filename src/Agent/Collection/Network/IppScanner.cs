using System.Net.Http.Headers;
using System.Text;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers network printers by probing port 631 on ARP-known neighbors with
/// an IPP (Internet Printing Protocol) Get-Printer-Attributes request, parsing
/// the response for printer name, make/model, and location. Source tag: "ipp".
/// Useful for reliably identifying printers regardless of vendor, since most
/// modern network printers support IPP even when other management protocols
/// (e.g. mDNS, SNMP) are disabled.
/// </summary>
public sealed class IppScanner : UnicastScannerBase
{
    public override string Name => "ipp";

    private static readonly HttpClient Http = new(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        }
    )
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    protected override async Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct)
    {
        try
        {
            string printerUri = $"ipp://{ip}:631/ipp/print";
            byte[] ippBytes = BuildIppRequest(printerUri);

            HttpRequestMessage request = new(HttpMethod.Post, $"http://{ip}:631/ipp/print")
            {
                Content = new ByteArrayContent(ippBytes),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");

            HttpResponseMessage response = await Http.SendAsync(request, ct);

            byte[] body = await response.Content.ReadAsByteArrayAsync(ct);
            if (body.Length < 8)
            {
                return null;
            }

            Dictionary<string, string> attributes = new();
            string? hostname = ExtractIppAttribute(body, "printer-name");
            string? model = ExtractIppAttribute(body, "printer-make-and-model");
            string? location = ExtractIppAttribute(body, "printer-location");
            // PWG 5110.1 extension (multi-valued); vendor-neutral firmware for printers whose web UI
            // we don't scrape. Absent on older printers — best-effort. Per RFC 8011 an attribute-less
            // Get-Printer-Attributes returns all attributes, so no request change is needed.
            string? firmware = ExtractIppAttribute(body, "printer-firmware-string-version");

            if (!string.IsNullOrWhiteSpace(model))
            {
                attributes["ipp.model"] = model;
            }

            if (!string.IsNullOrWhiteSpace(location))
            {
                attributes["ipp.location"] = location;
            }

            if (!string.IsNullOrWhiteSpace(firmware))
            {
                attributes["ipp.firmware"] = firmware;
            }

            return new DiscoveredDevice
            {
                IpAddress = ip,
                Hostname = string.IsNullOrWhiteSpace(hostname) ? null : hostname,
                Source = "ipp",
                Attributes = attributes,
            };
        }
        catch
        {
            return null;
        }
    }

    public static string? ExtractIppAttribute(byte[] data, string attributeName)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(attributeName);

        for (int i = 0; i + nameBytes.Length + 4 < data.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < nameBytes.Length; j++)
            {
                if (data[i + j] != nameBytes[j])
                {
                    match = false;
                    break;
                }
            }

            if (!match)
            {
                continue;
            }

            int valueOffset = i + nameBytes.Length;
            if (valueOffset + 2 > data.Length)
            {
                continue;
            }

            int valueLength = (data[valueOffset] << 8) | data[valueOffset + 1];
            int valueStart = valueOffset + 2;
            if (valueStart + valueLength > data.Length || valueLength <= 0 || valueLength > 256)
            {
                continue;
            }

            string value = Encoding.UTF8.GetString(data, valueStart, valueLength);
            if (value.All(c => c >= 0x20 && c < 0x7F))
            {
                return value;
            }
        }

        return null;
    }

    private static byte[] BuildIppRequest(string printerUri)
    {
        byte[] uriBytes = Encoding.UTF8.GetBytes(printerUri);

        List<byte> pkt = new()
        {
            0x02,
            0x00,
            0x00,
            0x0B,
            0x00,
            0x00,
            0x00,
            0x01,
            0x01,
            0x47,
            0x00,
            0x12,
        };
        pkt.AddRange("attributes-charset"u8.ToArray());
        pkt.Add(0x00);
        pkt.Add(0x05);
        pkt.AddRange("utf-8"u8.ToArray());

        pkt.Add(0x48);
        pkt.Add(0x00);
        pkt.Add(0x1B);
        pkt.AddRange("attributes-natural-language"u8.ToArray());
        pkt.Add(0x00);
        pkt.Add(0x02);
        pkt.AddRange("en"u8.ToArray());

        pkt.Add(0x45);
        pkt.Add(0x00);
        pkt.Add(0x0B);
        pkt.AddRange("printer-uri"u8.ToArray());
        pkt.Add((byte)(uriBytes.Length >> 8));
        pkt.Add((byte)(uriBytes.Length & 0xFF));
        pkt.AddRange(uriBytes);

        pkt.Add(0x03);

        return pkt.ToArray();
    }
}