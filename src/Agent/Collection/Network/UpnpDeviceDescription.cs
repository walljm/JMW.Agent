using System.Xml;
using System.Xml.Linq;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Parses a UPnP device-description document (UPnP Device Architecture 1.x) into identity fields.
/// The root <c>&lt;device&gt;</c> element carries self-declared, structured identity — the strongest
/// HTTP-reachable signal: <c>&lt;manufacturer&gt;</c>, <c>&lt;modelName&gt;</c>, <c>&lt;modelNumber&gt;</c>,
/// <c>&lt;serialNumber&gt;</c>, <c>&lt;friendlyName&gt;</c>. Namespace-agnostic (matches by local name) so
/// it tolerates the various <c>urn:schemas-upnp-org:device-1-0</c> / vendor-extended namespaces seen
/// in the wild.
/// </summary>
public static class UpnpDeviceDescription
{
    public static HttpDeepFields? Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XElement root;
        try
        {
            XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(new StringReader(xml), settings);
            XElement? parsed = XDocument.Load(reader).Root;
            if (parsed is null)
            {
                return null;
            }

            root = parsed;
        }
        catch (XmlException)
        {
            return null;
        }

        // The first <device> element (the root device); sub-devices in <deviceList> are ignored.
        XElement? device = root.Name.LocalName == "device"
            ? root
            : root.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
        if (device is null)
        {
            return null;
        }

        string? manufacturer = Child(device, "manufacturer");
        string? modelName = Child(device, "modelName");
        string? modelNumber = Child(device, "modelNumber");
        string? serial = Child(device, "serialNumber");
        string? friendlyName = Child(device, "friendlyName");

        // Prefer "<modelName> <modelNumber>" when the number adds detail the name doesn't already carry.
        string? model = modelName;
        if (modelName is not null
         && modelNumber is not null
         && !modelName.Contains(modelNumber, StringComparison.OrdinalIgnoreCase))
        {
            model = $"{modelName} {modelNumber}";
        }
        else if (modelName is null)
        {
            model = modelNumber;
        }

        HttpDeepFields fields = new(
            Vendor: manufacturer,
            Model: model,
            Firmware: null, // UPnP has no standard firmware element
            Serial: serial,
            FriendlyName: friendlyName
        );

        return fields.IsEmpty ? null : fields;
    }

    private static string? Child(XElement device, string localName)
    {
        // Direct children only — do not reach into a nested <deviceList> sub-device.
        XElement? el = device.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        string? value = el?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}