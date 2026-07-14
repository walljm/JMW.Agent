using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Device;

/// <summary>
/// Collects DNS query stats, DHCP scopes + leases, and DNS zone records
/// from a Technitium DNS Server instance via its HTTP API.
/// Implements IServiceCollector — configured via agent.json services[] rather
/// than hardcoded in Program.cs. The agent calls IdentifyServiceAsync() with
/// the primary forward zones as fingerprints; the server assigns a stable
/// ServiceId that persists even if Technitium moves to different hardware.
/// Identity fingerprints:
/// - Primary forward zones (e.g. "home.lan") — stable across host migrations
/// - DHCP subnets (e.g. "192.168.1.0/24") — secondary, if DHCP is enabled
/// - Reverse (.arpa) zones are excluded — they're derived from IP ranges and
/// change when the network is renumbered, not when the service changes identity
/// Auth: api-token (preferred) or username-password from Target.Credentials.
/// </summary>
public sealed class TechnitiumCollector : IServiceCollector, IDisposable
{
    public string ServiceType => "technitium-dns";

    public bool CanCollect(Target target) =>
        target.CollectorType is { } t && t.Equals("technitium-dns", StringComparison.OrdinalIgnoreCase);

    private const string LeaseTimeFmt = "MM/dd/yyyy HH:mm:ss"; // Technitium format

    // CA5359: accepting any server certificate is deliberate here. Technitium serves
    // its admin API over HTTPS with a self-signed certificate by default (no CA to
    // chain to) and redirects plain HTTP to it. The target is an operator-configured
    // endpoint authenticated by a bearer API token, so accept its certificate rather
    // than failing the TLS handshake. Scoped to this collector's client only — the
    // agent's own server channel uses separate SHA-256 pinning.
#pragma warning disable CA5359
    private static readonly SocketsHttpHandler _handler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        },
    };
#pragma warning restore CA5359

    private static readonly HttpClient _http = new(_handler, disposeHandler: false)
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly ILogger<TechnitiumCollector> _logger = AgentLog.CreateLogger<TechnitiumCollector>();

    // Per-URL token caches so one collector instance handles multiple targets.
    private readonly Dictionary<string, string> _cachedTokens = new();
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Main collection ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Fact>> CollectAsync(
        Target target,
        IServiceCollectionContext context,
        CancellationToken ct
    )
    {
        List<Fact> facts = new();
        string baseUrl = target.Endpoint.TrimEnd('/');

        string token;
        try { token = await GetTokenAsync(baseUrl, target.Credentials, ct); }
        catch (Exception ex)
        {
            TechnitiumCollectorLog.AuthFailed(_logger, ex, baseUrl);
            return facts;
        }

        // ── Phase 1: collect zones to build identity fingerprints ─────────────
        TechnitiumZonesList? zoneList = await CallAsync<TechnitiumZonesList>(
            baseUrl,
            "/api/zones/list",
            token,
            null,
            ct
        );

        // Forward primary zones only. Reverse (.arpa) zones are excluded because:
        // - They're derived from IP ranges, not from the server's logical identity
        // - They change when the network is renumbered, masking actual service continuity
        List<TechnitiumZonesList.ZoneRow> forwardZones = (zoneList?.Zones ?? [])
            .Where(z => !z.Disabled
             && z.Type == "Primary"
             && !z.Name.EndsWith(".arpa", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        // ── Phase 2: build fingerprints and resolve stable ServiceId ──────────
        List<ServiceFingerprint> fingerprints = new();
        foreach (TechnitiumZonesList.ZoneRow zone in forwardZones)
        {
            fingerprints.Add(new(ServiceFingerprintType.PrimaryZone, zone.Name.ToLowerInvariant()));
        }

        // If no forward zones exist (bare DHCP-only server), fall back to URL.
        if (fingerprints.Count == 0)
        {
            fingerprints.Add(new(ServiceFingerprintType.ServiceUrl, baseUrl));
        }

        string serviceId = await context.IdentifyServiceAsync(
            new ServiceProbe("technitium-dns", fingerprints),
            ct
        );

        // ── Phase 3: identity facts ───────────────────────────────────────────
        facts.Add(Fact.Create(ServicePaths.Type, [serviceId], "technitium-dns"));
        facts.AddIfPresent(ServicePaths.DeviceId, [serviceId], context.HostDeviceId);

        // ── Phase 4: full collection under the resolved serviceId ─────────────
        await CollectStatsAsync(serviceId, baseUrl, token, facts, ct);
        await CollectDhcpAsync(serviceId, baseUrl, token, facts, fingerprints, ct);
        await CollectZoneRecordsAsync(serviceId, baseUrl, token, forwardZones, facts, ct);

        return facts;
    }

    // ── Dashboard stats ───────────────────────────────────────────────────────

    private async Task CollectStatsAsync(
        string serverId,
        string baseUrl,
        string token,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        TechnitiumStats? stats = await CallAsync<TechnitiumStats>(
            baseUrl,
            "/api/dashboard/stats/get",
            token,
            new()
            {
                ["type"] = "LastDay",
                ["utc"] = "true",
            },
            ct
        );

        if (stats is null)
        {
            return;
        }

        facts.Add(Fact.Create(ServicePaths.DnsStatsTotalQueries, [serverId], stats.Stats.TotalQueries));
        facts.Add(Fact.Create(ServicePaths.DnsStatsTotalBlocked, [serverId], stats.Stats.TotalBlocked));

        if (stats.Stats.TotalQueries > 0)
        {
            facts.Add(
                Fact.Create(
                    ServicePaths.DnsStatsBlockedPct,
                    [serverId],
                    (double)stats.Stats.TotalBlocked / stats.Stats.TotalQueries * 100.0
                )
            );
        }

        foreach (TopEntry entry in stats.TopDomains.Take(10))
        {
            facts.Add(Fact.Create(ServicePaths.DnsTopQueried, [serverId, entry.Name], entry.Hits));
        }

        foreach (TopEntry entry in stats.TopBlockedDomains.Take(10))
        {
            facts.Add(Fact.Create(ServicePaths.DnsTopBlocked, [serverId, entry.Name], entry.Hits));
        }

        foreach (TopEntry entry in stats.TopClients.Take(10))
        {
            facts.Add(Fact.Create(ServicePaths.DnsTopClients, [serverId, entry.Name], entry.Hits));
        }
    }

    // ── DHCP ──────────────────────────────────────────────────────────────────

    private async Task CollectDhcpAsync(
        string serverId,
        string baseUrl,
        string token,
        List<Fact> facts,
        List<ServiceFingerprint> fingerprints,
        CancellationToken ct
    )
    {
        TechnitiumScopesList? scopeList = await CallAsync<TechnitiumScopesList>(
            baseUrl,
            "/api/dhcp/scopes/list",
            token,
            null,
            ct
        );

        if (scopeList?.Scopes is not { Count: > 0 } scopes)
        {
            return;
        }

        foreach (TechnitiumScopesList.ScopeRow scope in scopes)
        {
            string scopeName = scope.Name ?? "default";

            facts.Add(Fact.Create(ServicePaths.DhcpScopeEnabled, [serverId, scopeName], scope.Enabled));
            facts.Add(
                Fact.Create(ServicePaths.DhcpScopeStartAddress, [serverId, scopeName], scope.StartingAddress ?? "")
            );
            facts.Add(Fact.Create(ServicePaths.DhcpScopeEndAddress, [serverId, scopeName], scope.EndingAddress ?? ""));
            facts.Add(Fact.Create(ServicePaths.DhcpScopeSubnetMask, [serverId, scopeName], scope.SubnetMask ?? ""));

            TechnitiumScopeDetail? detail = await CallAsync<TechnitiumScopeDetail>(
                baseUrl,
                "/api/dhcp/scopes/get",
                token,
                new()
                {
                    ["name"] = scopeName,
                },
                ct
            );

            facts.AddIfPresent(ServicePaths.DhcpScopeGateway, [serverId, scopeName], detail?.RouterAddress);

            // Add the subnet as a secondary identity fingerprint.
            // Subnets survive host migration just like forward zones do.
            if (scope.NetworkAddress is { Length: > 0 } network && scope.SubnetMask is { Length: > 0 } mask)
            {
                string subnet = $"{network}/{MaskToCidr(mask)}";
                if (!fingerprints.Any(f => f.Type == ServiceFingerprintType.DhcpSubnet && f.Value == subnet))
                {
                    fingerprints.Add(new(ServiceFingerprintType.DhcpSubnet, subnet));
                }
            }
        }

        // Leases — keyed by scope then MAC, preserving scope context.
        TechnitiumLeasesList? leaseList = await CallAsync<TechnitiumLeasesList>(
            baseUrl,
            "/api/dhcp/leases/list",
            token,
            null,
            ct
        );

        if (leaseList?.Leases is null)
        {
            return;
        }

        foreach (TechnitiumLeasesList.LeaseRow lease in leaseList.Leases)
        {
            if (lease.HardwareAddress is not { Length: > 0 } mac)
            {
                continue;
            }

            string scopeName = lease.Scope ?? "default";

            facts.Add(Fact.Create(ServicePaths.DhcpLeaseIP, [serverId, scopeName, mac], lease.Address ?? ""));
            facts.Add(Fact.Create(ServicePaths.DhcpLeaseHostname, [serverId, scopeName, mac], lease.HostName ?? ""));
            facts.Add(Fact.Create(ServicePaths.DhcpLeaseType, [serverId, scopeName, mac], lease.Type ?? ""));

            if (DateTimeOffset.TryParseExact(
                lease.LeaseExpires,
                LeaseTimeFmt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTimeOffset exp
            ))
            {
                facts.Add(Fact.Create(ServicePaths.DhcpLeaseExpires, [serverId, scopeName, mac], exp.ToString("o")));
            }
        }
    }

    // Convert dotted-decimal subnet mask to CIDR prefix length
    private static int MaskToCidr(string mask)
    {
        if (!IPAddress.TryParse(mask, out IPAddress? ip))
        {
            return 0;
        }

        int bits = 0;
        byte[] bytes = ip.GetAddressBytes();
        foreach (byte b in bytes)
        {
            byte v = b;
            while (v != 0)
            {
                bits += v & 1;
                v >>= 1;
            }
        }

        return bits;
    }

    // ── DNS zones ─────────────────────────────────────────────────────────────

    private async Task CollectZoneRecordsAsync(
        string serverId,
        string baseUrl,
        string token,
        IEnumerable<TechnitiumZonesList.ZoneRow> forwardZones,
        List<Fact> facts,
        CancellationToken ct
    )
    {
        HashSet<string> seenIps = new(StringComparer.OrdinalIgnoreCase);

        foreach (TechnitiumZonesList.ZoneRow zone in forwardZones)
        {
            facts.Add(Fact.Create(ServicePaths.DnsZoneType, [serverId, zone.Name], zone.Type));

            // listZone=true returns every record in the zone — without it the API
            // only returns records at the zone apex. There is no server-side type
            // filter on this endpoint; we filter to A/AAAA/CNAME client-side.
            TechnitiumZoneRecordsList? recList = await CallAsync<TechnitiumZoneRecordsList>(
                baseUrl,
                "/api/zones/records/get",
                token,
                new()
                {
                    ["domain"] = zone.Name,
                    ["listZone"] = "true",
                },
                ct
            );

            if (recList?.Records is null)
            {
                continue;
            }

            foreach (TechnitiumZoneRecordsList.RecordRow rec in recList.Records)
            {
                if (rec.Disabled)
                {
                    continue;
                }

                string recName = rec.Name ?? "";

                // A/AAAA share the ipAddress rData shape; CNAME carries a target
                // name. Record type is a key dimension so all three coexist per host.
                if (rec.Type is "A" or "AAAA")
                {
                    if (rec.RData?.IpAddress is not { Length: > 0 } ip)
                    {
                        continue;
                    }

                    if (!seenIps.Add(ip))
                    {
                        continue; // dedup IPs that appear in multiple zones
                    }

                    string[] keys = [serverId, zone.Name, recName, rec.Type];
                    facts.Add(Fact.Create(ServicePaths.DnsZoneRecordIP, keys, ip));
                    facts.Add(Fact.Create(ServicePaths.DnsZoneRecordTTL, keys, rec.TTL));
                }
                else if (rec.Type is "CNAME")
                {
                    if (rec.RData?.Cname is not { Length: > 0 } cname)
                    {
                        continue;
                    }

                    string[] keys = [serverId, zone.Name, recName, rec.Type];
                    facts.Add(Fact.Create(ServicePaths.DnsZoneRecordTarget, keys, cname));
                    facts.Add(Fact.Create(ServicePaths.DnsZoneRecordTTL, keys, rec.TTL));
                }
            }
        }
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    private async Task<string> GetTokenAsync(
        string baseUrl,
        TargetCredentials? creds,
        CancellationToken ct
    )
    {
        if (creds is ApiTokenCredentials { Token: { Length: > 0 } t })
        {
            return t;
        }

        if (creds is not UsernamePasswordCredentials up)
        {
            throw new InvalidOperationException(
                $"TechnitiumCollector: no usable credentials for {baseUrl}. "
              + "Provide api-token or username-password credentials."
            );
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedTokens.TryGetValue(baseUrl, out string? cached))
            {
                return cached;
            }

            string token = await LoginAsync(baseUrl, up.Username, up.Password, ct);
            _cachedTokens[baseUrl] = token;
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static async Task<string> LoginAsync(
        string baseUrl,
        string username,
        string password,
        CancellationToken ct
    )
    {
        string url = $"{baseUrl}/api/user/login"
          + $"?user={Uri.EscapeDataString(username)}"
          + $"&pass={Uri.EscapeDataString(password)}"
          + $"&includeInfo=false";

        using HttpResponseMessage resp = await _http.PostAsync(url, null, ct);
        resp.EnsureSuccessStatusCode();

        LoginResponse body = await resp.Content.ReadFromJsonAsync<LoginResponse>(JsonOpts, ct)
         ?? throw new InvalidOperationException("Empty Technitium login response.");

        if (body.Status != "ok" || body.Token is not { Length: > 0 })
        {
            throw new UnauthorizedAccessException($"Technitium login failed for {baseUrl}.");
        }

        return body.Token;
    }

    // ── HTTP helper ───────────────────────────────────────────────────────────

    private async Task<T?> CallAsync<T>(
        string baseUrl,
        string path,
        string token,
        Dictionary<string, string>? query,
        CancellationToken ct
    ) where T : class
    {
        StringBuilder qs = new();
        qs.Append($"token={Uri.EscapeDataString(token)}");
        if (query is not null)
        {
            foreach ((string k, string v) in query)
            {
                qs.Append($"&{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
            }
        }

        try
        {
            using HttpResponseMessage resp = await _http.GetAsync($"{baseUrl}{path}?{qs}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            TechnitiumEnvelope? env = await resp.Content.ReadFromJsonAsync<TechnitiumEnvelope>(JsonOpts, ct);
            if (env is null || env.Status != "ok")
            {
                return null;
            }

            if (env.Response.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            return env.Response.Deserialize<T>(JsonOpts);
        }
        catch (Exception ex)
        {
            TechnitiumCollectorLog.ApiCallFailed(_logger, ex, path);
            return null;
        }
    }

    public void Dispose()
    {
        _tokenLock.Dispose();
    }

    // ── API response shapes ───────────────────────────────────────────────────

    private sealed class TechnitiumEnvelope
    {
        public string Status { get; set; } = "";
        public JsonElement Response { get; set; }
    }

    private sealed class LoginResponse
    {
        public string? Status { get; set; }
        public string? Token { get; set; }
    }

    private sealed class TechnitiumStats
    {
        public StatsBlock Stats { get; set; } = new();

        [JsonPropertyName("topDomains")]
        public List<TopEntry> TopDomains { get; set; } = [];

        [JsonPropertyName("topBlockedDomains")]
        public List<TopEntry> TopBlockedDomains { get; set; } = [];

        [JsonPropertyName("topClients")]
        public List<TopEntry> TopClients { get; set; } = [];

        public sealed class StatsBlock
        {
            [JsonPropertyName("totalQueries")]
            public long TotalQueries { get; set; }

            [JsonPropertyName("totalBlocked")]
            public long TotalBlocked { get; set; }
        }
    }

    private sealed class TopEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("hits")]
        public long Hits { get; set; }
    }

    private sealed class TechnitiumScopesList
    {
        [JsonPropertyName("scopes")]
        public List<ScopeRow> Scopes { get; set; } = [];

        public sealed class ScopeRow
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("enabled")]
            public bool Enabled { get; set; }

            [JsonPropertyName("startingAddress")]
            public string? StartingAddress { get; set; }

            [JsonPropertyName("endingAddress")]
            public string? EndingAddress { get; set; }

            [JsonPropertyName("subnetMask")]
            public string? SubnetMask { get; set; }

            [JsonPropertyName("networkAddress")]
            public string? NetworkAddress { get; set; }
        }
    }

    private sealed class TechnitiumScopeDetail
    {
        [JsonPropertyName("routerAddress")]
        public string? RouterAddress { get; set; }
    }

    private sealed class TechnitiumLeasesList
    {
        [JsonPropertyName("leases")]
        public List<LeaseRow> Leases { get; set; } = [];

        public sealed class LeaseRow
        {
            [JsonPropertyName("scope")]
            public string? Scope { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("hardwareAddress")]
            public string? HardwareAddress { get; set; }

            [JsonPropertyName("address")]
            public string? Address { get; set; }

            [JsonPropertyName("hostName")]
            public string? HostName { get; set; }

            [JsonPropertyName("leaseObtained")]
            public string? LeaseObtained { get; set; }

            [JsonPropertyName("leaseExpires")]
            public string? LeaseExpires { get; set; }
        }
    }

    private sealed class TechnitiumZonesList
    {
        [JsonPropertyName("zones")]
        public List<ZoneRow> Zones { get; set; } = [];

        public sealed class ZoneRow
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("type")]
            public string Type { get; set; } = "";

            [JsonPropertyName("disabled")]
            public bool Disabled { get; set; }
        }
    }

    private sealed class TechnitiumZoneRecordsList
    {
        [JsonPropertyName("records")]
        public List<RecordRow> Records { get; set; } = [];

        public sealed class RecordRow
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("ttl")]
            public int TTL { get; set; }

            [JsonPropertyName("disabled")]
            public bool Disabled { get; set; }

            // rData is an object whose fields depend on the record type
            // (A → ipAddress, NS → nameServer, SOA → primaryNameServer, …).
            [JsonPropertyName("rData")]
            public RecordData? RData { get; set; }
        }

        public sealed class RecordData
        {
            [JsonPropertyName("ipAddress")]
            public string? IpAddress { get; set; } // A / AAAA

            [JsonPropertyName("cname")]
            public string? Cname { get; set; } // CNAME target
        }
    }
}

internal static partial class TechnitiumCollectorLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Technitium auth failed for {BaseUrl}.")]
    public static partial void AuthFailed(ILogger logger, Exception ex, string baseUrl);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Technitium API call to {Path} failed.")]
    public static partial void ApiCallFailed(ILogger logger, Exception ex, string path);
}