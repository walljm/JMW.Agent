namespace JMW.Discovery.Agent.Collection.Device.OnHub;

/// <summary>Device-identity signals mined from a station's mDNS / DNS-SD advertisements.</summary>
public sealed record OnHubDnsSdResult(
    string? Friendly,
    string? Model,
    string? DeviceType,
    string? CastId,
    IReadOnlyList<string> Services,
    // Raw _googlecast TXT values, captured opaque/unparsed — see FactPaths.DiscoveredCastCapabilities
    // for why: Google never published ca='s bit layout, and st=/rs= are transient state, not identity.
    string? CastCapabilities = null, // ca=
    string? CastStatus = null, // st=
    string? CastRunningApp = null // rs= — currently-running receiver app/title, empty when idle
);

/// <summary>
/// The self-contained DNS-SD (mDNS) sub-parser for a Google Wifi station, extracted from
/// <see cref="OnHubStations" /> (review item F6) so the fiddly instance-name grammar can be
/// read and unit-tested on its own. <see cref="OnHubStations" /> keeps the IP-join
/// orchestration and calls <see cref="Parse" /> once per station.
/// </summary>
public static class OnHubDnsSd
{
    /// <summary>
    /// Mines a station's <c>dns_sd_features</c> (mDNS) for friendly name, model,
    /// device type, the stable Google Cast device id, the advertised service types, and
    /// (under _googlecast only) the raw capabilities/status/running-app TXT values.
    /// </summary>
    public static OnHubDnsSdResult Parse(TextNode station)
    {
        SortedSet<string> services = new(StringComparer.Ordinal);
        string? friendly = null;
        string? castFriendly = null;
        string? model = null;
        string? deviceType = null;
        string? castId = null;
        string? castCapabilities = null;
        string? castStatus = null;
        string? castRunningApp = null;
        bool hasAirplay = false;

        foreach (TextNode node in station.ChildrenNamed("dns_sd_features"))
        {
            string? key = node.ScalarOf("key");
            string? svc = key is { Length: > 0 } ? ServiceType(key) : null;
            if (svc is not null)
            {
                services.Add(svc);
                if (svc.StartsWith("_airplay", StringComparison.Ordinal))
                {
                    hasAirplay = true;
                }
            }

            if (key is { Length: > 0 })
            {
                deviceType ??= DeviceTypeFromKey(key);

                // Stable Cast device id — only from _googlecast (airplay/raop ids are
                // randomized/synthetic and must not anchor identity).
                if (svc is not null && svc.StartsWith("_googlecast", StringComparison.Ordinal))
                {
                    castId ??= CastIdFromKey(key);
                }

                // AirPlay/RAOP carry the friendly name in the instance label, not an
                // fn= value: "<id>@Great Room Audio._raop._tcp" or "MacBook._airplay._tcp".
                if (svc is not null
                 && (svc.StartsWith("_raop", StringComparison.Ordinal)
                     || svc.StartsWith("_airplay", StringComparison.Ordinal)))
                {
                    friendly ??= InstanceNameFromKey(key);
                }
            }

            if (node.ScalarOf("value") is { Length: > 0 } value)
            {
                if (value.StartsWith("fn=", StringComparison.Ordinal))
                {
                    // fn= (cast) is the most authoritative friendly name — prefer it.
                    castFriendly ??= value[3..];
                }
                else if (value.StartsWith("model=", StringComparison.Ordinal))
                {
                    // _device-info / _airplay model= is a real model (e.g. "Mac15,3").
                    model ??= value[6..];
                }
                else if (value.StartsWith("md=", StringComparison.Ordinal)
                 && svc is not null
                 && svc.StartsWith("_googlecast", StringComparison.Ordinal))
                {
                    // md= is ONLY a model under _googlecast. Under _raop it is codec
                    // metadata ("md=0,1,2") — never a device model.
                    model ??= value[3..];
                }
                else if (svc is not null && svc.StartsWith("_googlecast", StringComparison.Ordinal))
                {
                    // ca=/st=/rs= are Cast-specific — gated to _googlecast so a same-named
                    // key under a different service never gets misread as Cast state.
                    if (value.StartsWith("ca=", StringComparison.Ordinal))
                    {
                        castCapabilities ??= value[3..];
                    }
                    else if (value.StartsWith("st=", StringComparison.Ordinal))
                    {
                        castStatus ??= value[3..];
                    }
                    else if (value.StartsWith("rs=", StringComparison.Ordinal))
                    {
                        castRunningApp ??= value[3..];
                    }
                }
            }
        }

        // Apple devices advertise _airplay + a Mac*/iPhone*/etc model but no cast type.
        if (deviceType is null && hasAirplay)
        {
            deviceType = "apple-device";
        }

        // A cast fn= friendly name is only trustworthy when this same station also resolved a
        // stable Cast id. The OnHub's dns_sd cache attributes an advertisement to whatever
        // station held the advertised IP when it was cached, so it smears a cast fn= onto an
        // unrelated station — verified live: a Home Assistant host station picked up a Nest
        // speaker's "fn=Guest Room Audio" with no cast id or device type of its own, which then
        // promoted onto the host device as its display name. The AirPlay/RAOP `friendly` is
        // taken from this station's own service-instance label (not a cross-referenced TXT
        // value), so it is self-contained and kept regardless.
        string? trustedCastFriendly = castId is not null ? castFriendly : null;

        return new OnHubDnsSdResult(
            Clean(trustedCastFriendly ?? friendly),
            Clean(model),
            deviceType,
            castId,
            [.. services],
            Clean(castCapabilities),
            Clean(castStatus),
            Clean(castRunningApp)
        );
    }

    /// <summary>
    /// Extracts the stable Google Cast device id — the trailing hex token of a cast
    /// instance label: "Google-Nest-Mini-&lt;32hex&gt;._googlecast._tcp.local" →
    /// "&lt;32hex&gt;". Returns null unless ≥16 lowercase-hex chars follow the last '-'.
    /// </summary>
    private static string? CastIdFromKey(string key)
    {
        int svc = key.IndexOf("._", StringComparison.Ordinal);
        string instance = svc >= 0 ? key[..svc] : key;

        int dash = instance.LastIndexOf('-');
        if (dash <= 0 || dash == instance.Length - 1)
        {
            return null;
        }

        string suffix = instance[(dash + 1)..];
        if (suffix.Length < 16)
        {
            return null;
        }

        foreach (char c in suffix)
        {
            if (!Uri.IsHexDigit(c))
            {
                return null;
            }
        }

        return suffix.ToLowerInvariant();
    }

    /// <summary>
    /// Extracts the human instance label from a service-instance key, stripping a
    /// leading "&lt;id&gt;@" (AirPlay/RAOP deviceid prefix) and the service suffix.
    /// "5E7064@WonderWoman's MacBook._raop._tcp.local" → "WonderWoman's MacBook".
    /// </summary>
    private static string? InstanceNameFromKey(string key)
    {
        int svc = key.IndexOf("._", StringComparison.Ordinal);
        string instance = svc >= 0 ? key[..svc] : key;

        int at = instance.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            instance = instance[(at + 1)..];
        }

        // AirPlay speaker-group names often carry a trailing '+' marker.
        instance = instance.TrimEnd('+').Trim();
        return instance.Length > 0 ? instance : null;
    }

    /// <summary>
    /// Extracts the mDNS service type ("_googlecast._tcp") from a service-instance
    /// name ("Nest-Audio-&lt;hex&gt;._googlecast._tcp.local").
    /// </summary>
    private static string? ServiceType(string key)
    {
        string trimmed = key.EndsWith(".local", StringComparison.Ordinal) ? key[..^6] : key;
        string[] segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int start = Array.FindIndex(segments, s => s.StartsWith('_'));
        if (start < 0)
        {
            return null;
        }

        return string.Join('.', segments[start..]);
    }

    /// <summary>
    /// Derives a device type from a cast-style instance name of the form
    /// "&lt;Type&gt;-&lt;32-hex-id&gt;" (e.g. "Nest-Audio-1294…" → "Nest-Audio").
    /// Returns null for instance names that don't carry a type prefix.
    /// </summary>
    private static string? DeviceTypeFromKey(string key)
    {
        int svc = key.IndexOf("._", StringComparison.Ordinal);
        string instance = svc >= 0 ? key[..svc] : key;

        int dash = instance.LastIndexOf('-');
        if (dash <= 0 || dash == instance.Length - 1)
        {
            return null;
        }

        string suffix = instance[(dash + 1)..];
        if (suffix.Length < 16)
        {
            return null;
        }

        foreach (char c in suffix)
        {
            if (!Uri.IsHexDigit(c))
            {
                return null;
            }
        }

        return instance[..dash];
    }

    /// <summary>
    /// Non-empty, non-masked scalar, or null. (Mirrors OnHubStations.Clean — the
    /// masked-value rule the whole OnHub mapper shares.)
    /// </summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('*', StringComparison.Ordinal))
        {
            return null;
        }

        return value.Trim();
    }
}