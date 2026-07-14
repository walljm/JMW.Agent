using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class TerrainApi
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/terrain/dhcp", ListDhcpLeases)
            .RequireAuthorization(ReadPolicy.Name);
        app.MapGet("/report/terrain/dns", ListDnsRecords)
            .RequireAuthorization(ReadPolicy.Name);
    }

    // ── DHCP ──────────────────────────────────────────────────────────────────

    private static Task<IResult> ListDhcpLeases(
        NpgsqlDataSource db,
        string? after,
        string? q,
        int limit = DefaultLimit,
        CancellationToken ct = default
    ) =>
        KeysetPage.RunAsync<DhcpLeaseItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 2,
            fetch: (parts, lim) => QueryDhcpAsync(db, q, parts?[0], parts?[1], lim, ct)
        );

    public static async Task<(List<DhcpLeaseItem> Items, string? NextCursor)> QueryDhcpAsync(
        NpgsqlDataSource db,
        string? search,
        string? afterDevice,
        string? afterLease,
        int limit,
        CancellationToken ct
    )
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<(string Device, string? ObserverHostname, string Mac, string? Oui, string? OuiCountry, string? Ip, string?
            ClientHostname, string? ExpiresAt
          , string? Source)> rows = await conn.ListDhcpLeasesAsync(
                string.IsNullOrWhiteSpace(search) ? null : search,
                afterDevice,
                afterLease,
                limit + 1,
                ct
            )
            .ToListAsync(ct);

        string? nextCursor = KeysetPage.NextCursor(rows, limit, r => [r.Device, r.Mac]);

        List<DhcpLeaseItem> items = rows.Select(r => new DhcpLeaseItem(
                    Device: r.Device,
                    ObserverHostname: r.ObserverHostname,
                    Mac: r.Mac,
                    Oui: r.Oui,
                    OuiCountry: r.OuiCountry,
                    Ip: r.Ip,
                    ClientHostname: r.ClientHostname,
                    ExpiresAt: r.ExpiresAt,
                    Source: r.Source
                )
            )
            .ToList();

        return (items, nextCursor);
    }

    // ── DNS Records ───────────────────────────────────────────────────────────

    private static Task<IResult> ListDnsRecords(
        NpgsqlDataSource db,
        string? after,
        string? q,
        int limit = DefaultLimit,
        CancellationToken ct = default
    ) =>
        KeysetPage.RunAsync<DnsRecordItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 4,
            fetch: (parts, lim) =>
                QueryDnsAsync(db, q, parts?[0], parts?[1], parts?[2], parts?[3], lim, ct)
        );

    public static async Task<(List<DnsRecordItem> Items, string? NextCursor)> QueryDnsAsync(
        NpgsqlDataSource db,
        string? search,
        string? afterService,
        string? afterZone,
        string? afterRecord,
        string? afterRtype,
        int limit,
        CancellationToken ct
    )
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<(string Service, string Zone, string Record, string Rtype, string? Value, int? Ttl)> rows =
            await conn.ListDnsRecordsAsync(
                    string.IsNullOrWhiteSpace(search) ? null : search,
                    afterService,
                    afterZone,
                    afterRecord,
                    afterRtype,
                    limit + 1,
                    ct
                )
                .ToListAsync(ct);

        string? nextCursor = KeysetPage.NextCursor(rows, limit, r => [r.Service, r.Zone, r.Record, r.Rtype]);

        List<DnsRecordItem> items = rows.Select(r => new DnsRecordItem(
                    Service: r.Service,
                    Zone: r.Zone,
                    Record: r.Record,
                    Type: r.Rtype,
                    Value: r.Value,
                    Ttl: r.Ttl
                )
            )
            .ToList();

        return (items, nextCursor);
    }
}

public sealed record DhcpLeaseItem(
    string Device,
    string? ObserverHostname,
    string Mac,
    string? Oui,
    string? OuiCountry,
    string? Ip,
    string? ClientHostname,
    string? ExpiresAt,
    string? Source
);

public sealed record DnsRecordItem(
    string Service,
    string Zone,
    string Record,
    string Type,
    string? Value,
    int? Ttl
);