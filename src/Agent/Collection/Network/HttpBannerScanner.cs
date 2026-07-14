using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.RegularExpressions;

using JMW.Discovery.Agent.Collection.Network.Recog;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Network;

/// <summary>
/// Discovers hosts by probing common web ports on ARP-known neighbors. Captures the
/// "Server" response header and HTML &lt;title&gt; as loose identity hints, hashes the
/// device's favicon (MD5 for Recog, Shodan mmh3 for external pivoting), then resolves device
/// identity on-agent by matching those signals against the embedded Recog corpus. Source tag:
/// "http-banner". Acts as a catch-all for routers, NAS boxes, printers, cameras, and other
/// devices with a management web page that aren't identified by a more specific protocol scanner.
/// </summary>
public sealed class HttpBannerScanner : UnicastScannerBase
{
    public override string Name => "http-banner";

    private static readonly ILogger<HttpBannerScanner> Log = AgentLog.CreateLogger<HttpBannerScanner>();

    // Loaded once per process: the full Recog fingerprint corpus + the resolver over it.
    private static readonly HttpIdentityResolver Resolver = new(RecogCorpus.LoadEmbedded());

    // Core web-management ports. Port is only a weak prior — it selects the scheme and narrows
    // candidates; identity comes from the response content. Noisy/device-specific ports
    // (AMT 16992/3, TR-069 7547, cPanel 208x) are intentionally excluded until there is a
    // per-scanner config hook to gate them.
    private static readonly int[] DefaultPorts =
        [80, 443, 8080, 8443, 8000, 8888, 8008, 8081, 10000, 4443, 5000, 5001, 8006];

    private readonly int[] ports;

    public HttpBannerScanner()
        : this(DefaultPorts)
    {
    }

    /// <summary>Constructs the scanner with an explicit port set (used by tests; a config hook later).</summary>
    public HttpBannerScanner(int[] ports)
    {
        this.ports = ports;
    }

    // Ports that speak TLS by convention; everything else is probed as plain HTTP.
    private static readonly HashSet<int> HttpsPorts = [443, 8443, 4443, 5001, 8006];

    // Favicons are small; cap the read so a hostile/oversized response can't blow up memory.
    private const int MaxFaviconBytes = 512 * 1024;

    // .NET's default TLS 1.2 cipher suite list on Linux (OpenSSL) excludes plain RSA-key-exchange
    // suites (no forward secrecy) — confirmed live: an HP OfficeJet Pro 6970 only offers
    // TLS_RSA_WITH_AES_256_GCM_SHA384 (curl negotiates it fine; .NET's SocketsHttpHandler
    // rejected it by default, producing SSL_ERROR_SSL / "sslv3 alert handshake failure" on
    // every probe, silently making the device look completely unreachable). This scanner
    // inherently talks to old/weak embedded web servers — the same reason cert validation is
    // already fully disabled below — so explicitly allowing the legacy RSA suites here is a
    // narrowly-scoped, appropriate trade-off, not a broad security downgrade.
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "CipherSuitesPolicy is not trimmed/AOT in this project."
    )]
    [SuppressMessage(
        "Interoperability",
        "CA1416",
        Justification = "This agent only ships for linux-x64/linux-arm64; CipherSuitesPolicy is supported there."
    )]
    [SuppressMessage(
        "Security",
        "CA5359",
        Justification = "Deliberate: this scanner fingerprints arbitrary LAN devices with self-signed/expired "
            + "certs (routers, printers, cameras); it never exchanges sensitive data, so the identity check "
            + "cert validation exists for doesn't apply here."
    )]
    private static readonly HttpClient Http = new(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            // Without this, a gzip/deflate/br-compressed response (common on embedded web
            // servers — confirmed live on an HP OfficeJet Pro 6970's root page) is read as raw
            // compressed bytes and decoded as UTF-8 text, producing garbage: no title match, no
            // Server/Recog signal, no favicon — the entire probe silently looks like "nothing
            // here" even though the device answered every request. HttpClient decompresses
            // transparently once this is set; no other code here needs to change.
            AutomaticDecompression = DecompressionMethods.All,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                CipherSuitesPolicy = new CipherSuitesPolicy(
                    [
                        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
                        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
                        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
                        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
                        TlsCipherSuite.TLS_RSA_WITH_AES_256_GCM_SHA384,
                        TlsCipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256,
                        TlsCipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA256,
                        TlsCipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA256,
                        TlsCipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA,
                        TlsCipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA,
                    ]
                ),
            },
        }
    )
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    protected override async Task<DiscoveredDevice?> ProbeHostAsync(string ip, CancellationToken ct)
    {
        foreach (int port in ports)
        {
            DiscoveredDevice? device = await TryProbePortAsync(ip, port, ct);
            if (device is not null)
            {
                return device;
            }
        }

        return null;
    }

    private static async Task<DiscoveredDevice?> TryProbePortAsync(string ip, int port, CancellationToken ct)
    {
        try
        {
            string scheme = HttpsPorts.Contains(port) ? "https" : "http";
            string url = $"{scheme}://{ip}:{port}/";

            using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            string body = "";
            using (Stream stream = await response.Content.ReadAsStreamAsync(ct))
            {
                byte[] buffer = new byte[8192];
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                body = Encoding.UTF8.GetString(buffer, 0, read);
            }

            Dictionary<string, string> attributes = new();

            if (response.Headers.TryGetValues("Server", out IEnumerable<string>? serverValues))
            {
                attributes["http.server"] = string.Join(", ", serverValues);
            }

            Match titleMatch = Regex.Match(body, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
            if (titleMatch.Success)
            {
                attributes["http.title"] = titleMatch.Groups[1].Value.Trim();
            }

            attributes["http.status"] = ((int)response.StatusCode).ToString();
            Uri baseUri = response.RequestMessage?.RequestUri ?? new Uri(url);
            attributes["http.url"] = baseUri.ToString();

            await AddFaviconHashesAsync(attributes, baseUri, body, ct);

            // WWW-Authenticate (present on 401s) — a strong, device-specific identity signal.
            string? wwwAuth = null;
            if (response.Headers.WwwAuthenticate.Count > 0)
            {
                AuthenticationHeaderValue auth = response.Headers.WwwAuthenticate.First();
                wwwAuth = string.IsNullOrEmpty(auth.Parameter) ? auth.Scheme : $"{auth.Scheme} {auth.Parameter}";
            }

            await ResolveIdentityAsync(attributes, baseUri, body, wwwAuth, ct);

            return new DiscoveredDevice
            {
                IpAddress = ip,
                Source = "http-banner",
                Attributes = attributes,
            };
        }
        catch (Exception ex)
        {
            HttpBannerScannerLog.ProbeFailed(Log, ex, ip, port);
            return null;
        }
    }

    /// <summary>
    /// Resolves the favicon (from a &lt;link rel="icon"&gt; href, falling back to /favicon.ico),
    /// fetches it, and adds MD5 + Shodan mmh3 hashes. Best-effort: any failure leaves the
    /// attributes untouched — the banner probe still succeeds without a favicon.
    /// </summary>
    private static async Task AddFaviconHashesAsync(
        Dictionary<string, string> attributes,
        Uri baseUri,
        string body,
        CancellationToken ct
    )
    {
        try
        {
            Uri faviconUri = ResolveFaviconUri(baseUri, body);

            using HttpResponseMessage response = await Http.GetAsync(faviconUri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            byte[] bytes = await ReadCappedAsync(response, ct);
            if (bytes.Length == 0)
            {
                return;
            }

            attributes["http.favicon.md5"] = FaviconHash.Md5Hex(bytes);
            attributes["http.favicon.mmh3"] = FaviconHash.ShodanHash(bytes).ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            // No favicon is not an error; leave attributes as-is.
        }
    }

    // Reference to a UPnP/device-description document hinted in the landing-page HTML. Only fetched
    // when the page itself points at it (a "hinted" follow-up, not a blind description-path sweep).
    // Captures a relative path or an absolute URL token (stops at quotes/space/angle brackets).
    private static readonly Regex UpnpDescriptionRef = new(
        @"([^\s""'<>()=]*(?:rootDesc|description|gatedesc|igddesc|WFADevice|wps_device|MediaServerDevDesc)[^\s""'<>()=]*\.xml)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>
    /// Resolves device identity from the collected banner signals, then runs an adaptive follow-up
    /// (UPnP device description, when the page hints one) and merges the deeper, self-declared fields
    /// over the banner guesses. The result is written as <c>http.identity.*</c> attributes that
    /// promote to the shared typed Vendor/Model/Firmware/DeviceType/OS facts (ranked below
    /// device-specific protocol scanners), plus serial, friendly name, confidence, and provenance.
    /// </summary>
    private static async Task ResolveIdentityAsync(
        Dictionary<string, string> attributes,
        Uri baseUri,
        string body,
        string? wwwAuth,
        CancellationToken ct
    )
    {
        HttpIdentitySignals signals = new(
            Server: attributes.GetValueOrDefault("http.server"),
            Title: attributes.GetValueOrDefault("http.title"),
            WwwAuthenticate: wwwAuth,
            FaviconMd5: attributes.GetValueOrDefault("http.favicon.md5")
        );

        HttpIdentity? shallow = Resolver.Resolve(signals);

        // HP/Epson span many models/generations — a rich fetch can legitimately come back empty
        // (older/newer LEDM revision, EWS page moved, this particular unit doesn't support it) since
        // every fetch/parse step is best-effort and null-safe rather than throwing. When it does,
        // fall through to the generic follow-ups (Brother/SyncThru/Canon don't apply, but the UPnP
        // description fallback is vendor-agnostic and still worth trying) instead of settling for
        // nothing beyond the banner-level guess.
        PrinterFollowUps.PrinterDetails? printerDetails = null;
        HttpFollowUpResult? deep = null;
        if (PrinterFollowUps.IsHp(shallow, signals))
        {
            (HttpDeepFields? hpIdentity, printerDetails) = await PrinterFollowUps.FetchHpAsync(baseUri, GetTextCappedAsync, ct);
            deep = hpIdentity is { IsEmpty: false } ? new HttpFollowUpResult(hpIdentity, "hp-ledm", 0.95) : null;
        }
        else if (PrinterFollowUps.IsEpson(shallow, signals))
        {
            (HttpDeepFields? epsonIdentity, printerDetails) = await PrinterFollowUps.FetchEpsonAsync(baseUri, GetTextCappedAsync, ct);
            deep = epsonIdentity is { IsEmpty: false } ? new HttpFollowUpResult(epsonIdentity, "epson-ews", 0.95) : null;
        }

        deep ??= await RunFollowUpsAsync(baseUri, body, shallow, ct);

        HttpIdentity? identity = HttpIdentityResolver.Combine(shallow, deep);
        if (identity is not null)
        {
            SetIfPresent(attributes, "http.identity.vendor", identity.Vendor);
            SetIfPresent(attributes, "http.identity.model", identity.Model);
            SetIfPresent(attributes, "http.identity.firmware", identity.Firmware);
            SetIfPresent(attributes, "http.identity.type", identity.DeviceType);
            SetIfPresent(attributes, "http.identity.os", identity.Os);
            SetIfPresent(attributes, "http.identity.serial", identity.Serial);
            SetIfPresent(attributes, "http.identity.name", identity.Name);
            attributes["http.identity.confidence"] = identity.Confidence.ToString(CultureInfo.InvariantCulture);
            attributes["http.identity.source"] = identity.Provenance;
        }

        if (printerDetails is not null)
        {
            SetIfPresent(attributes, "printer.status", printerDetails.Status);
            SetIfPresent(attributes, "printer.alerts", printerDetails.Alerts);
            SetIfPresent(attributes, "printer.consumables", printerDetails.Consumables);
            SetIfPresent(attributes, "printer.product_number", printerDetails.ProductNumber);
        }
    }

    private static void SetIfPresent(Dictionary<string, string> attributes, string key, string? value)
    {
        if (value is not null)
        {
            attributes[key] = value;
        }
    }

    /// <summary>
    /// Runs adaptive follow-up probes, returning the first that yields fields (or null). A
    /// vendor-specific printer page (triggered by the banner-resolved vendor) takes priority over the
    /// generic UPnP description, since it carries serial/firmware the description lacks.
    /// </summary>
    private static async Task<HttpFollowUpResult?> RunFollowUpsAsync(
        Uri baseUri,
        string body,
        HttpIdentity? shallow,
        CancellationToken ct
    )
    {
        foreach (PrinterFollowUps.Descriptor printer in PrinterFollowUps.All)
        {
            if (!printer.Applies(shallow))
            {
                continue;
            }

            try
            {
                // An empty Path means "parse the landing page we already fetched" (no extra request).
                string? content = string.IsNullOrEmpty(printer.Path)
                    ? body
                    : await GetTextCappedAsync(new Uri(baseUri, printer.Path), ct);
                if (content is not null && printer.Parse(content) is { IsEmpty: false } printerFields)
                {
                    return new HttpFollowUpResult(printerFields, printer.Source, 0.95);
                }
            }
            catch
            {
                // Best-effort; fall through to other follow-ups.
            }

            break; // only the first matching vendor applies
        }

        Uri? descriptionUrl = FindUpnpDescriptionUrl(baseUri, body);
        if (descriptionUrl is not null)
        {
            try
            {
                string? xml = await GetTextCappedAsync(descriptionUrl, ct);
                if (xml is not null && UpnpDeviceDescription.Parse(xml) is { IsEmpty: false } fields)
                {
                    return new HttpFollowUpResult(fields, "upnp", 0.95);
                }
            }
            catch
            {
                // Follow-up is best-effort; fall through to the banner identity.
            }
        }

        return null;
    }

    public static Uri? FindUpnpDescriptionUrl(Uri baseUri, string body)
    {
        Match m = UpnpDescriptionRef.Match(body);
        return m.Success && Uri.TryCreate(baseUri, m.Groups[1].Value, out Uri? url) ? url : null;
    }

    private static async Task<string?> GetTextCappedAsync(Uri url, CancellationToken ct)
    {
        using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        byte[] bytes = await ReadCappedAsync(response, ct);
        return bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
    }

    public static Uri ResolveFaviconUri(Uri baseUri, string body)
    {
        Match link = Regex.Match(
            body,
            "<link[^>]+rel=[\"'][^\"']*icon[^\"']*[\"'][^>]*>",
            RegexOptions.IgnoreCase
        );
        if (link.Success)
        {
            Match href = Regex.Match(link.Value, "href=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
            if (href.Success
             && Uri.TryCreate(baseUri, href.Groups[1].Value.Trim(), out Uri? resolved))
            {
                return resolved;
            }
        }

        return new Uri(baseUri, "/favicon.ico");
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using MemoryStream ms = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxFaviconBytes)
            {
                return [];
            }
        }

        return ms.ToArray();
    }
}

internal static partial class HttpBannerScannerLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "HTTP banner probe of {Ip}:{Port} failed.")]
    internal static partial void ProbeFailed(ILogger logger, Exception ex, string ip, int port);
}