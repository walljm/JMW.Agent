namespace JMW.Discovery.Agent.Collection.Device.OnHub;

/// <summary>Device-identity signals mined from a station's UPnP device-description attributes.</summary>
public sealed record OnHubUpnpResult(string? FriendlyName, string? Manufacturer, string? Model);

/// <summary>
/// Parses a station's <c>upnp_attribute</c> (key/value) entries — the OnHub's own UPnP
/// device-description query results, standard fields: friendlyName, manufacturer, modelName,
/// modelNumber. Mirrors UpnpDeviceDescription's combining rule for Model so the
/// two sources agree when both are present.
/// </summary>
public static class OnHubUpnp
{
    public static OnHubUpnpResult Parse(TextNode station)
    {
        string? friendlyName = null;
        string? manufacturer = null;
        string? modelName = null;
        string? modelNumber = null;

        foreach (TextNode attr in station.ChildrenNamed("upnp_attribute"))
        {
            string? key = attr.ScalarOf("key");
            string? value = Clean(attr.ScalarOf("value"));
            if (key is null || value is null)
            {
                continue;
            }

            switch (key)
            {
                case "friendlyName":
                    friendlyName ??= value;
                    break;
                case "manufacturer":
                    manufacturer ??= value;
                    break;
                case "modelName":
                    modelName ??= value;
                    break;
                case "modelNumber":
                    modelNumber ??= value;
                    break;
            }
        }

        // Prefer "<modelName> <modelNumber>" when the number adds detail the name doesn't
        // already carry (mirrors UpnpDeviceDescription's SSDP-description combining rule).
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

        return new OnHubUpnpResult(friendlyName, manufacturer, model);
    }

    /// <summary>Non-empty, non-masked scalar, or null (mirrors OnHubStations.Clean).</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('*', StringComparison.Ordinal))
        {
            return null;
        }

        return value.Trim();
    }
}