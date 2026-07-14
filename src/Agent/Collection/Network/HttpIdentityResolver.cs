using JMW.Discovery.Agent.Collection.Network.Recog;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>The HTTP signals collected from a host that feed identity resolution.</summary>
public sealed record HttpIdentitySignals(
    string? Server,
    string? Title,
    string? WwwAuthenticate,
    string? FaviconMd5
);

/// <summary>
/// Resolved device identity plus an overall confidence (0..1) and a compact field-&gt;signal
/// provenance string (e.g. <c>vendor:server,model:favicon</c>). Serial/Name come only from
/// adaptive follow-ups (e.g. UPnP), so the shallow resolve leaves them null.
/// </summary>
public sealed record HttpIdentity(
    string? Vendor,
    string? Model,
    string? Firmware,
    string? DeviceType,
    string? Os,
    string? Serial,
    string? Name,
    double Confidence,
    string Provenance
);

/// <summary>Identity fields extracted by an adaptive follow-up probe (e.g. UPnP description XML).</summary>
public sealed record HttpDeepFields(
    string? Vendor = null,
    string? Model = null,
    string? Firmware = null,
    string? Serial = null,
    string? FriendlyName = null
)
{
    public bool IsEmpty => Vendor is null && Model is null && Firmware is null && Serial is null && FriendlyName is null;
}

/// <summary>The outcome of an adaptive follow-up: extracted fields, the signal name, and its confidence.</summary>
public sealed record HttpFollowUpResult(HttpDeepFields Fields, string Source, double Confidence);

/// <summary>
/// Turns collected HTTP signals into device identity by matching each signal against its Recog
/// database and fusing the results. Runs on the agent so a match can later drive adaptive probing
/// (phase 4). Higher-confidence signals win per field; two independent signals agreeing on
/// vendor/model boost confidence. Recog already yields canonical vendor/product strings, so no
/// name normalization happens here (that is a separate, deliberate concern).
/// </summary>
public sealed class HttpIdentityResolver
{
    private readonly RecogCorpus corpus;

    public HttpIdentityResolver(RecogCorpus corpus)
    {
        this.corpus = corpus;
    }

    public HttpIdentity? Resolve(HttpIdentitySignals signals)
    {
        // Match each present signal against its DB, keeping strongest-first order.
        List<(string Name, double Confidence, RecogMatch Match)> matched = [];
        AddMatch(matched, signals.FaviconMd5, RecogCorpus.FaviconMd5, "favicon", 0.85);
        AddMatch(matched, signals.WwwAuthenticate, RecogCorpus.HttpWwwAuth, "wwwauth", 0.80);
        AddMatch(matched, signals.Server, RecogCorpus.HttpServer, "server", 0.75);
        AddMatch(matched, signals.Title, RecogCorpus.HtmlTitle, "title", 0.60);

        if (matched.Count == 0)
        {
            return null;
        }

        List<string> provenance = [];
        HashSet<string> contributors = new(StringComparer.Ordinal);

        string? Pick(string field, params string[] recogKeys)
        {
            foreach ((string name, _, RecogMatch match) in matched)
            {
                foreach (string key in recogKeys)
                {
                    if (match.Fields.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                    {
                        provenance.Add($"{field}:{name}");
                        contributors.Add(name);
                        return value;
                    }
                }
            }

            return null;
        }

        string? vendor = Pick("vendor", "hw.vendor", "service.vendor", "os.vendor");
        string? model = Pick("model", "hw.model", "hw.product", "service.product", "os.product");
        string? firmware = Pick("firmware", "hw.version", "os.version", "service.version");
        string? deviceType = Pick("type", "hw.device", "os.device", "service.device");
        string? os = Pick("os", "os.product", "os.family");

        if (contributors.Count == 0)
        {
            // Matched a fingerprint but it carried none of the identity fields we map.
            return null;
        }

        double baseConfidence = matched
            .Where(m => contributors.Contains(m.Name))
            .Max(m => m.Confidence);

        // Corroboration: two independent signals asserting the same vendor or model raise confidence.
        bool corroborated =
            (vendor is not null && CountAsserting(matched, vendor, "hw.vendor", "service.vendor", "os.vendor") >= 2)
            || (model is not null && CountAsserting(matched, model, "hw.model", "hw.product", "service.product", "os.product") >= 2);

        double confidence = Math.Round(Math.Min(0.95, baseConfidence + (corroborated ? 0.10 : 0.0)), 2);

        return new HttpIdentity(vendor, model, firmware, deviceType, os, null, null, confidence, string.Join(",", provenance));
    }

    /// <summary>
    /// Merges the shallow (banner-signal) identity with an adaptive follow-up result. Follow-up
    /// fields (self-declared, structured) override banner guesses; confidence is the max of the two;
    /// provenance is rebuilt so each field names the signal that ultimately supplied it.
    /// </summary>
    public static HttpIdentity? Combine(HttpIdentity? shallow, HttpFollowUpResult? deep)
    {
        if (deep is null || deep.Fields.IsEmpty)
        {
            return shallow; // no useful follow-up — return the banner identity unchanged
        }

        // field -> source, seeded from the shallow provenance ("vendor:server,model:favicon").
        Dictionary<string, string> source = new(StringComparer.Ordinal);
        if (shallow is not null)
        {
            foreach (string pair in shallow.Provenance.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = pair.IndexOf(':');
                if (colon > 0)
                {
                    source[pair[..colon]] = pair[(colon + 1)..];
                }
            }
        }

        HttpDeepFields d = deep.Fields;
        string ds = deep.Source;

        string? Merge(string field, string? deepValue, string? shallowValue)
        {
            if (deepValue is not null)
            {
                source[field] = ds;
                return deepValue;
            }

            return shallowValue;
        }

        string? vendor = Merge("vendor", d.Vendor, shallow?.Vendor);
        string? model = Merge("model", d.Model, shallow?.Model);
        string? firmware = Merge("firmware", d.Firmware, shallow?.Firmware);
        string? serial = Merge("serial", d.Serial, shallow?.Serial);
        string? name = Merge("name", d.FriendlyName, shallow?.Name);
        string? deviceType = shallow?.DeviceType; // follow-ups here don't assert a friendly type
        string? os = shallow?.Os;

        double confidence = Math.Max(shallow?.Confidence ?? 0.0, deep.Confidence);

        List<string> provenance = [];
        void Note(string field, string? value)
        {
            if (value is not null && source.TryGetValue(field, out string? s))
            {
                provenance.Add($"{field}:{s}");
            }
        }

        Note("vendor", vendor);
        Note("model", model);
        Note("firmware", firmware);
        Note("type", deviceType);
        Note("os", os);
        Note("serial", serial);
        Note("name", name);

        return new HttpIdentity(vendor, model, firmware, deviceType, os, serial, name, confidence, string.Join(",", provenance));
    }

    private void AddMatch(
        List<(string, double, RecogMatch)> matched,
        string? input,
        string matchType,
        string name,
        double confidence
    )
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        RecogMatch? match = corpus.Match(matchType, input);
        if (match is not null)
        {
            matched.Add((name, confidence, match));
        }
    }

    private static int CountAsserting(
        List<(string Name, double Confidence, RecogMatch Match)> matched,
        string value,
        params string[] recogKeys
    )
    {
        int count = 0;
        foreach ((string _, double _, RecogMatch match) in matched)
        {
            foreach (string key in recogKeys)
            {
                if (match.Fields.TryGetValue(key, out string? v)
                 && string.Equals(v, value, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }
}