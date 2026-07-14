using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Vendor-specific printer follow-up probes: when the banner identity points at a known printer
/// vendor, fetch that vendor's structured identity page and extract model / serial / firmware.
///
/// Most single-page vendors are covered by <see cref="Descriptor"/> entries in <see cref="All"/>
/// (Brother dt/dd HTML, Samsung SyncThru JSON — page formats verified against open-source scrapers).
/// Vendors whose web UIs are unstable across models (Canon, Lexmark, Xerox, Ricoh, Kyocera, Konica
/// Minolta, Sharp) stay best-effort/model-only here.
///
/// HP and Epson get a richer, multi-page fetch (<see cref="FetchHpAsync"/>, <see cref="FetchEpsonAsync"/>)
/// instead of a single Descriptor: both expose status/consumables/hardware detail across several pages
/// (HP's LEDM <c>/DevMgmt/*.xml</c> trio — confirmed identical schema across an OfficeJet Pro and a
/// LaserJet M209dw; Epson's EWS <c>/PRESENTATION/ADVANCED/*</c> pages — confirmed live against an
/// SC-P900, contradicting the earlier assumption that Epson doesn't expose serial/firmware over HTTP),
/// so a single Path+Parse pair can't capture everything worth having. Every fetch/parse step here is
/// best-effort and null-safe (never throws on an unexpected/missing shape) since HP and Epson each
/// span many more models and firmware generations than were directly verified — a fetch coming back
/// empty just means "no rich detail this time," not a broken probe; <see cref="HttpBannerScanner"/>
/// falls back to the banner-level guess (and the generic UPnP description follow-up) when that happens.
/// </summary>
public static class PrinterFollowUps
{
    /// <summary>A vendor follow-up: when <see cref="Applies"/>, fetch <see cref="Path"/> and <see cref="Parse"/> it.</summary>
    public sealed record Descriptor(string Source, Func<HttpIdentity?, bool> Applies, string Path, Func<string, HttpDeepFields?> Parse);

    /// <summary>
    /// Status/consumables/hardware detail from a printer's rich management API — kept separate from
    /// <see cref="HttpDeepFields"/> (generic cross-vendor identity) since it's printer-specific
    /// telemetry, written as its own <c>printer.*</c> attributes rather than merged into identity.
    /// </summary>
    public sealed record PrinterDetails(
        string? Status = null,
        string? Alerts = null,
        string? Consumables = null,
        string? ProductNumber = null
    )
    {
        public bool IsEmpty => Status is null && Alerts is null && Consumables is null && ProductNumber is null;
    }

    /// <summary>Fetches and reads a URL's body as text, or null on any failure — supplied by the caller (<see cref="HttpBannerScanner"/>) so this class stays HttpClient-free and testable.</summary>
    public delegate Task<string?> FetchText(Uri url, CancellationToken ct);

    // A follow-up whose Path is "" parses the already-fetched landing page instead of a new request.
    // HP and Epson are handled by FetchHpAsync/FetchEpsonAsync instead — see class doc comment.
    public static readonly IReadOnlyList<Descriptor> All =
    [
        new("brother", id => VendorOrModelContains(id, "brother"),
            "/general/information.html?kind=item", ParseBrother),
        new("syncthru", id => VendorOrModelContains(id, "samsung", "syncthru"),
            "/sws/app/information/home/home.json", ParseSyncThru),
        // Best-effort, model-focused (web UIs vary by generation; serial/firmware are unreliable here
        // and should come from SNMP/IPP). Canon identity is on the landing page (Path "").
        new("canon", id => VendorOrModelContains(id, "canon"), "", ParseCanon),
    ];

    private static readonly string[] HpNeedles = ["hp", "hewlett", "laserjet", "officejet", "deskjet"];
    private static readonly string[] EpsonNeedles = ["epson"];

    /// <summary>
    /// True if EITHER the Recog-resolved shallow identity OR the raw banner signals (title,
    /// server header) mention HP. Recog's shallow resolve alone isn't enough — confirmed live: an
    /// HP LaserJet M209dw's <c>Server</c> header is <c>Virata-EmWeb/R6_2_1</c> (HP licenses
    /// Allegro's EmWeb as its EWS backend), which Recog resolves to vendor=null/model="EmWeb" —
    /// no "hp"/"laserjet" substring anywhere in the resolved identity — even though the page
    /// <c>&lt;title&gt;</c> plainly says "HP LaserJet M209dw". Checking the raw signals too
    /// catches this without depending on Recog's corpus having an exact fingerprint for every
    /// firmware/model variant.
    /// </summary>
    public static bool IsHp(HttpIdentity? id, HttpIdentitySignals? signals = null) =>
        ContainsAny(id, signals, HpNeedles);

    public static bool IsEpson(HttpIdentity? id, HttpIdentitySignals? signals = null) =>
        ContainsAny(id, signals, EpsonNeedles);

    private static bool VendorOrModelContains(HttpIdentity? id, params string[] needles) => ContainsAny(id, null, needles);

    private static bool ContainsAny(HttpIdentity? id, HttpIdentitySignals? signals, string[] needles)
    {
        string haystack = string.Join(
            ' ',
            id?.Vendor,
            id?.Model,
            signals?.Server,
            signals?.Title
        ).ToLowerInvariant();

        return haystack.Length > 0 && needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));
    }

    /// <summary>
    /// Fetches HP's LEDM management API (confirmed live across both an OfficeJet Pro and a
    /// LaserJet M209dw — same schema regardless of ink/toner engine) — <c>ProductConfigDyn.xml</c>
    /// for identity + product number, <c>ProductStatusDyn.xml</c> for status/alerts,
    /// <c>ConsumableConfigDyn.xml</c> for ink/toner levels. Best-effort per document: a failed
    /// fetch/parse just omits that document's fields rather than failing the whole probe.
    /// </summary>
    public static async Task<(HttpDeepFields? Identity, PrinterDetails? Details)> FetchHpAsync(
        Uri baseUri,
        FetchText fetch,
        CancellationToken ct
    )
    {
        string? configXml = await TryFetch(fetch, baseUri, "/DevMgmt/ProductConfigDyn.xml", ct);
        HttpDeepFields? identity = configXml is not null ? ParseHpLedm(configXml) : null;
        string? productNumber = configXml is not null ? ParseHpProductNumber(configXml) : null;

        string? statusXml = await TryFetch(fetch, baseUri, "/DevMgmt/ProductStatusDyn.xml", ct);
        (string? status, string? alerts) = statusXml is not null
            ? ParseHpStatus(statusXml)
            : (null, null);

        string? consumablesXml = await TryFetch(fetch, baseUri, "/DevMgmt/ConsumableConfigDyn.xml", ct);
        string? consumables = consumablesXml is not null ? ParseHpConsumables(consumablesXml) : null;

        PrinterDetails details = new(status, alerts, consumables, productNumber);
        return (identity, details.IsEmpty ? null : details);
    }

    /// <summary>HP LEDM: identity under <c>ProductInformation</c> — MakeAndModel / SerialNumber / Version→Revision.</summary>
    public static HttpDeepFields? ParseHpLedm(string xml)
    {
        XElement? root = TryLoad(xml);
        XElement? info = root?.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "ProductInformation");
        if (info is null)
        {
            return null;
        }

        string? model = Child(info, "MakeAndModel");
        string? serial = Child(info, "SerialNumber");
        XElement? version = info.Elements().FirstOrDefault(e => e.Name.LocalName == "Version");
        string? firmware = version?.Elements().FirstOrDefault(e => e.Name.LocalName == "Revision")?.Value.Trim();

        HttpDeepFields fields = new(
            Vendor: model is not null ? "HP" : null,
            Model: model,
            Firmware: string.IsNullOrEmpty(firmware) ? null : firmware,
            Serial: serial
        );
        return fields.IsEmpty ? null : fields;
    }

    /// <summary>HP LEDM: the SKU/part number under <c>ProductInformation/ProductNumber</c> (e.g. "T0F29A") — distinct from the MakeAndModel display name.</summary>
    public static string? ParseHpProductNumber(string xml)
    {
        XElement? root = TryLoad(xml);
        XElement? info = root?.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "ProductInformation");
        return info is null ? null : Child(info, "ProductNumber");
    }

    /// <summary>
    /// HP LEDM <c>ProductStatusDyn.xml</c>: <c>psdyn:Status/pscat:StatusCategory</c> entries are the
    /// printer's own declared state categories (pipe-joined as-is — e.g. "inPowerSave"). Alerts come
    /// from <c>psdyn:AlertTable/psdyn:Alert</c>, filtered to non-Info severity (Warning/Error/Critical)
    /// so routine notices (e.g. "genuineHP") don't drown out things actually worth surfacing.
    /// </summary>
    public static (string? Status, string? Alerts) ParseHpStatus(string xml)
    {
        XElement? root = TryLoad(xml);
        if (root is null)
        {
            return (null, null);
        }

        List<string> categories = root.Descendants()
            .Where(e => e.Name.LocalName == "Status")
            .Select(e => e.Elements().FirstOrDefault(c => c.Name.LocalName == "StatusCategory")?.Value.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        List<string> alerts = [];
        foreach (XElement alert in root.Descendants().Where(e => e.Name.LocalName == "Alert"))
        {
            string? severity = alert.Elements().FirstOrDefault(e => e.Name.LocalName == "Severity")?.Value.Trim();
            string? id = alert.Elements().FirstOrDefault(e => e.Name.LocalName == "ProductStatusAlertID")?.Value.Trim();
            if (string.IsNullOrEmpty(id) || string.Equals(severity, "Info", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            alerts.Add($"{severity ?? "Alert"}:{id}");
        }

        return (
            categories.Count == 0 ? null : string.Join('|', categories),
            alerts.Count == 0 ? null : string.Join('|', alerts.Distinct(StringComparer.Ordinal))
        );
    }

    /// <summary>
    /// HP LEDM <c>ConsumableConfigDyn.xml</c>: one <c>ccdyn:ConsumableInfo</c> per supply. Level
    /// prefers a numeric <c>ConsumablePercentageLevelRemaining</c> (laser toner) when present, else
    /// the qualitative <c>ConsumableLifeState/MeasuredQuantityState</c> (inkjet cartridges report
    /// "ok"/"veryLow" rather than an exact percentage). The non-replaceable printhead is skipped —
    /// it isn't a "running low" supply.
    /// </summary>
    public static string? ParseHpConsumables(string xml)
    {
        XElement? root = TryLoad(xml);
        if (root is null)
        {
            return null;
        }

        List<string> entries = [];
        foreach (XElement info in root.Descendants().Where(e => e.Name.LocalName == "ConsumableInfo"))
        {
            string? type = Child(info, "ConsumableTypeEnum");
            if (string.Equals(type, "printhead", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? label = Child(info, "ConsumableLabelCode");
            if (label is null)
            {
                continue;
            }

            string? percent = Child(info, "ConsumablePercentageLevelRemaining");
            string? level = percent is not null
                ? $"{percent}%"
                : info.Elements().FirstOrDefault(e => e.Name.LocalName == "ConsumableLifeState")
                    is { } lifeState
                    ? Child(lifeState, "MeasuredQuantityState") ?? Child(lifeState, "ConsumableState")
                    : null;

            if (level is not null)
            {
                entries.Add($"{label}:{level}");
            }
        }

        return entries.Count == 0 ? null : string.Join('|', entries);
    }

    /// <summary>
    /// Fetches Epson's EWS "Advanced" UI (confirmed live against an SC-P900 — contradicts the prior
    /// assumption that Epson doesn't expose serial/firmware over HTTP): the landing page for the
    /// model name, Product Status for status/serial/firmware/ink levels, Hardware Status for
    /// connectivity alerts. Best-effort per page.
    /// </summary>
    public static async Task<(HttpDeepFields? Identity, PrinterDetails? Details)> FetchEpsonAsync(
        Uri baseUri,
        FetchText fetch,
        CancellationToken ct
    )
    {
        string? landingHtml = await TryFetch(fetch, baseUri, "/PRESENTATION/ADVANCED/COMMON/TOP", ct);
        string? model = landingHtml is not null ? ParseEpsonModelName(landingHtml) : null;

        string? prtInfoHtml = await TryFetch(fetch, baseUri, "/PRESENTATION/ADVANCED/INFO_PRTINFO/TOP", ct);
        HttpDeepFields? identity = null;
        PrinterDetails? details = null;
        if (prtInfoHtml is not null)
        {
            (string? serial, string? firmware, string? status, string? consumables) = ParseEpsonPrinterInfo(prtInfoHtml);
            identity = new HttpDeepFields(Vendor: "Epson", Model: model, Firmware: firmware, Serial: serial);
            details = new PrinterDetails(Status: status, Consumables: consumables);
        }
        else if (model is not null)
        {
            identity = new HttpDeepFields(Vendor: "Epson", Model: model);
        }

        string? hwHtml = await TryFetch(fetch, baseUri, "/PRESENTATION/ADVANCED/INFO_BEHAVIORINFO/TOP", ct);
        string? alerts = hwHtml is not null ? ParseEpsonHardwareAlerts(hwHtml) : null;
        if (alerts is not null)
        {
            details = details is null ? new PrinterDetails(Alerts: alerts) : details with { Alerts = alerts };
        }

        return (identity is { IsEmpty: false } ? identity : null, details is { IsEmpty: false } ? details : null);
    }

    private static readonly Regex EpsonModelName = new(
        "<h1[^>]*id=[\"']model_name[\"'][^>]*>(.*?)</h1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled
    );

    public static string? ParseEpsonModelName(string html)
    {
        Match m = EpsonModelName.Match(html);
        if (!m.Success)
        {
            return null;
        }

        string value = StripTags(m.Groups[1].Value);
        return value.Length == 0 ? null : value;
    }

    // Epson's "legend" fieldset shape — <legend>Label</legend> ... <div class="preserve-white-space">Value</div> —
    // used for the top-level Printer Status line (not a dt/dd or th/td pair like LabeledValue covers).
    private static Regex EpsonLegendValue(string legend) => new(
        $@"<legend>{Regex.Escape(legend)}</legend>.*?class=""preserve-white-space"">(.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    // Each ink tank: <li class='tank'>...<img class='inkst' src='...Icn_low...'>...<div class='clrname'>CODE</div></li>.
    // Non-greedy per-<li> so the low-ink icon check stays scoped to its own tank.
    private static readonly Regex EpsonInkTank = new(
        @"<li class='tank'>(.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled
    );

    private static readonly Regex EpsonClrName = new(
        @"<div class='clrname'>(.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled
    );

    /// <summary>
    /// Epson Product Status page: Serial Number / Firmware from the generic labeled-value pairs,
    /// Printer Status from its own legend/fieldset shape, ink levels from the tank-icon list (only a
    /// structural "low ink icon present or not" signal is reliable here — the tank fill images'
    /// pixel height isn't a documented percentage, so it's deliberately not used).
    /// </summary>
    public static (string? Serial, string? Firmware, string? Status, string? Consumables) ParseEpsonPrinterInfo(string html)
    {
        string? serial = null;
        string? firmware = null;
        foreach (Match m in LabeledValue.Matches(html))
        {
            string label = StripTags(m.Groups[1].Value);
            string value = StripTags(m.Groups[2].Value);
            if (value.Length == 0)
            {
                continue;
            }

            if (serial is null && label.Contains("serial number", StringComparison.OrdinalIgnoreCase))
            {
                serial = value;
            }
            else if (firmware is null && label.Contains("firmware", StringComparison.OrdinalIgnoreCase))
            {
                firmware = value;
            }
        }

        Match statusMatch = EpsonLegendValue("Printer Status").Match(html);
        string? status = statusMatch.Success ? StripTags(statusMatch.Groups[1].Value) : null;
        status = string.IsNullOrEmpty(status) ? null : status;

        List<string> tanks = [];
        foreach (Match tank in EpsonInkTank.Matches(html))
        {
            Match clrName = EpsonClrName.Match(tank.Value);
            if (!clrName.Success)
            {
                continue; // the maintenance box <li> has no clrname — not a color tank, skip it
            }

            string code = StripTags(clrName.Groups[1].Value);
            if (code.Length == 0)
            {
                continue;
            }

            bool low = tank.Value.Contains("Icn_low", StringComparison.OrdinalIgnoreCase);
            tanks.Add($"{code}:{(low ? "low" : "ok")}");
        }

        return (serial, firmware, status, tanks.Count == 0 ? null : string.Join('|', tanks));
    }

    /// <summary>Epson Hardware Status page: labeled lines (e.g. "Wi-Fi: Working normally.") — only the ones NOT reporting a normal/working state are surfaced as alerts.</summary>
    public static string? ParseEpsonHardwareAlerts(string html)
    {
        List<string> alerts = [];
        foreach (Match m in LabeledValue.Matches(html))
        {
            // Epson's label span already carries its own trailing "&nbsp;:" (e.g. "Wi-Fi&nbsp;:") —
            // trim it so it doesn't double up with the separator added below.
            string label = StripTags(m.Groups[1].Value).TrimEnd(':', ' ', '\u00A0');
            string value = StripTags(m.Groups[2].Value);
            if (label.Length == 0 || value.Length == 0)
            {
                continue;
            }

            if (!Regex.IsMatch(value, "normal", RegexOptions.IgnoreCase))
            {
                alerts.Add($"{label}:{value}");
            }
        }

        return alerts.Count == 0 ? null : string.Join('|', alerts);
    }

    private static async Task<string?> TryFetch(FetchText fetch, Uri baseUri, string path, CancellationToken ct)
    {
        try
        {
            return await fetch(new Uri(baseUri, path), ct);
        }
        catch
        {
            return null;
        }
    }

    // Brother: an HTML definition list, <dt>label</dt><dd>value</dd>. Serial + main firmware are
    // reliable here; model is not (get it from the status page / SNMP), so we leave it null.
    private static readonly Regex BrotherDtDd = new(
        @"<dt[^>]*>(.*?)</dt>\s*<dd[^>]*>(.*?)</dd>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled
    );

    public static HttpDeepFields? ParseBrother(string html)
    {
        string? serial = null;
        string? firmware = null;
        foreach (Match m in BrotherDtDd.Matches(html))
        {
            string label = StripTags(m.Groups[1].Value);
            string value = StripTags(m.Groups[2].Value);
            if (value.Length == 0)
            {
                continue;
            }

            if (serial is null && label.Contains("serial", StringComparison.OrdinalIgnoreCase))
            {
                serial = value;
            }
            else if (firmware is null && label.StartsWith("Main Firmware", StringComparison.OrdinalIgnoreCase))
            {
                firmware = value;
            }
        }

        HttpDeepFields fields = new(Firmware: firmware, Serial: serial);
        return fields.IsEmpty ? null : fields;
    }

    /// <summary>Samsung/HP-Samsung SyncThru: <c>identity.model_name</c> / <c>identity.serial_num</c> (no firmware in JSON).</summary>
    public static HttpDeepFields? ParseSyncThru(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("identity", out JsonElement identity)
             || identity.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? model = GetString(identity, "model_name");
            string? serial = GetString(identity, "serial_num");
            HttpDeepFields fields = new(Model: model, Serial: serial);
            return fields.IsEmpty ? null : fields;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Canon Remote UI: identity in <span id="deviceName">, formatted "[Name] / [Model] / [location]"
    // (iR-ADV also embeds "[Model] - [Serial]" in the first segment). High variance across lines —
    // best-effort model, opportunistic serial.
    private static readonly Regex CanonDeviceName = new(
        "<span[^>]*id=[\"']deviceName[\"'][^>]*>(.*?)</span>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled
    );

    public static HttpDeepFields? ParseCanon(string html)
    {
        Match m = CanonDeviceName.Match(html);
        if (!m.Success)
        {
            return null;
        }

        string[] parts = StripTags(m.Groups[1].Value).Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        // "[Name] / [Model] / [location]" → model is the middle segment; single-segment → that segment.
        string model = parts.Length >= 2 ? parts[1] : parts[0];

        // Opportunistic serial: "iR-ADV C5235 - JWC04988" in the first segment.
        string? serial = null;
        int dash = parts[0].IndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0)
        {
            string candidate = parts[0][(dash + 3)..].Trim();
            if (candidate.Length >= 4)
            {
                serial = candidate;
            }
        }

        HttpDeepFields fields = new(Vendor: "Canon", Model: model, Serial: serial);
        return fields.IsEmpty ? null : fields;
    }

    // Generic labeled-value pairs (dt/dd, th/td, td/td) for simple info tables.
    private static readonly Regex LabeledValue = new(
        @"<(?:dt|th|td)[^>]*>(.*?)</(?:dt|th|td)>\s*<(?:dd|td)[^>]*>(.*?)</(?:dd|td)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled
    );

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(el.GetString())
            ? el.GetString()
            : null;

    private static XElement? TryLoad(string xml)
    {
        try
        {
            XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader).Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? Child(XElement parent, string localName)
    {
        string? value = parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // Epson's labels carry literal HTML entities (e.g. "Wi-Fi&nbsp;:") — decode them too, not just
    // strip tags, or "&nbsp;" leaks verbatim into the extracted text.
    private static string StripTags(string s) => WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", "")).Trim();
}