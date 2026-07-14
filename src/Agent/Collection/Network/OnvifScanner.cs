using System.Net;
using System.Text;
using System.Xml.Linq;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers ONVIF-compliant IP cameras and NVRs by probing ports 80/8080 on
/// ARP-known neighbors. Posts GetDeviceInformation and GetSystemDateAndTime SOAP
/// requests against known ONVIF device-service paths; a successful response
/// yields manufacturer/model/firmware/serial, while a 401 response still
/// confirms an ONVIF device is present. Source tag: "onvif".
/// </summary>
public sealed class OnvifScanner : UnicastScannerBase
{
    public override string Name => "onvif";

    private static readonly HttpClient Http = new(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        }
    )
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    private static readonly string GetDeviceInformationSoap =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
      + "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\" "
      + "xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\">"
      + "<soap:Header/>"
      + "<soap:Body><tds:GetDeviceInformation/></soap:Body>"
      + "</soap:Envelope>";

    private static readonly string GetSystemDateAndTimeSoap =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
      + "<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\" "
      + "xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\">"
      + "<soap:Header/>"
      + "<soap:Body><tds:GetSystemDateAndTime/></soap:Body>"
      + "</soap:Envelope>";

    private static readonly string[] OnvifPaths = ["/onvif/device_service", "/onvif/Device"];
    private static readonly int[] OnvifPorts = [80, 8080];

    protected override async Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct)
    {
        foreach (int port in OnvifPorts)
        {
            foreach (string path in OnvifPaths)
            {
                string url = port == 80
                    ? $"http://{ip}{path}"
                    : $"http://{ip}:{port}{path}";

                DiscoveredDevice? device = await TryGetDeviceInformationAsync(ip, url, ct);
                if (device is not null)
                {
                    return device;
                }
            }
        }

        foreach (int port in OnvifPorts)
        {
            foreach (string path in OnvifPaths)
            {
                string url = port == 80
                    ? $"http://{ip}{path}"
                    : $"http://{ip}:{port}{path}";

                DiscoveredDevice? device = await TryGetSystemDateAndTimeAsync(ip, url, ct);
                if (device is not null)
                {
                    return device;
                }
            }
        }

        return null;
    }

    private static async Task<DiscoveredDevice?> TryGetDeviceInformationAsync(
        string ip,
        string url,
        CancellationToken ct
    )
    {
        try
        {
            using StringContent content = new(GetDeviceInformationSoap, Encoding.UTF8, "application/soap+xml");
            HttpResponseMessage response = await Http.PostAsync(url, content, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new DiscoveredDevice
                {
                    IpAddress = ip,
                    Source = "onvif",
                    Attributes = new Dictionary<string, string>
                    {
                        ["onvif.auth_required"] = "true",
                    },
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            XDocument doc = XDocument.Parse(body);

            string? manufacturer = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Manufacturer")?.Value;
            string? model = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Model")?.Value;
            string? firmware = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "FirmwareVersion")?.Value;
            string? serial = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "SerialNumber")?.Value;
            string? hardwareId = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "HardwareId")?.Value;

            if (string.IsNullOrWhiteSpace(manufacturer) && string.IsNullOrWhiteSpace(model))
            {
                return null;
            }

            Dictionary<string, string> attributes = new();

            if (!string.IsNullOrWhiteSpace(manufacturer))
            {
                attributes["onvif.manufacturer"] = manufacturer;
            }

            if (!string.IsNullOrWhiteSpace(model))
            {
                attributes["onvif.model"] = model;
            }

            if (!string.IsNullOrWhiteSpace(firmware))
            {
                attributes["onvif.firmware"] = firmware;
            }

            if (!string.IsNullOrWhiteSpace(serial))
            {
                attributes["onvif.serial"] = serial;
            }

            if (!string.IsNullOrWhiteSpace(hardwareId))
            {
                attributes["onvif.hardware_id"] = hardwareId;
            }

            string? hostname = null;
            if (!string.IsNullOrWhiteSpace(manufacturer) && !string.IsNullOrWhiteSpace(model))
            {
                hostname = $"{manufacturer} {model}";
            }
            else if (!string.IsNullOrWhiteSpace(manufacturer))
            {
                hostname = manufacturer;
            }
            else if (!string.IsNullOrWhiteSpace(model))
            {
                hostname = model;
            }

            return new DiscoveredDevice
            {
                IpAddress = ip,
                Hostname = hostname,
                Source = "onvif",
                Attributes = attributes,
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<DiscoveredDevice?> TryGetSystemDateAndTimeAsync(
        string ip,
        string url,
        CancellationToken ct
    )
    {
        try
        {
            using StringContent content = new(GetSystemDateAndTimeSoap, Encoding.UTF8, "application/soap+xml");
            HttpResponseMessage response = await Http.PostAsync(url, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            XDocument doc = XDocument.Parse(body);

            XElement? dateTimeResponse =
                doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "GetSystemDateAndTimeResponse");
            if (dateTimeResponse is null)
            {
                return null;
            }

            return new DiscoveredDevice
            {
                IpAddress = ip,
                Source = "onvif",
                Attributes = new Dictionary<string, string>
                {
                    ["onvif.auth_required"] = "true",
                },
            };
        }
        catch
        {
            return null;
        }
    }
}