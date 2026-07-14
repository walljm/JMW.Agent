using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class ServicesApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/services", ListServices)
            .RequireAuthorization(ReadPolicy.Name);

        app.MapGet("/report/services/{id}", GetService)
            .RequireAuthorization(ReadPolicy.Name);
    }

    private static Task<IResult> ListServices(
        NpgsqlDataSource db,
        string? after,
        string? type,
        string? q,
        int limit = DefaultLimit,
        CancellationToken ct = default
    ) =>
        KeysetPage.RunAsync<ServiceListItem>(
            after,
            limit,
            MaxLimit,
            cursorArity: 1,
            fetch: (parts, lim) => QueryAsync(db, type, q, parts?[0], lim, ct)
        );

    public static async Task<(List<ServiceListItem> Items, string? NextCursor)> QueryAsync(
        NpgsqlDataSource db,
        string? type,
        string? q,
        string? afterService,
        int limit,
        CancellationToken ct
    )
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<(string Service, string? Type, string? DeviceId, string? CaStatus, DateTimeOffset? RootNotAfter, long?
            TotalQueries, double? BlockedPct)> rows =
            await conn.ListServicesAsync(type, q, afterService, limit + 1, ct).ToListAsync(ct);

        string? nextCursor = KeysetPage.NextCursor(rows, limit, r => [r.Service]);

        List<ServiceListItem> items = rows.Select(r => new ServiceListItem(
                    Service: r.Service,
                    Type: r.Type,
                    DeviceId: r.DeviceId,
                    CaStatus: r.CaStatus,
                    RootNotAfter: r.RootNotAfter?.UtcDateTime,
                    TotalQueries: r.TotalQueries,
                    BlockedPct: r.BlockedPct
                )
            )
            .ToList();

        return (items, nextCursor);
    }

    private static async Task<IResult> GetService(
        string id,
        NpgsqlDataSource db,
        CancellationToken ct
    )
    {
        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        (string Service, string? ServiceId, string? Type, string? DeviceId, string? CaStatus, string? CaAddress, string?
            RootSubjectDn, DateTimeOffset? RootNotBefore, DateTimeOffset? RootNotAfter, string? RootFingerprint, string?
            IntSubjectDn, DateTimeOffset? IntNotBefore, DateTimeOffset? IntNotAfter, long? TotalQueries, long?
            TotalBlocked, double? BlockedPct) detail = await conn.GetServiceDetailAsync(id, ct).FirstOrDefaultAsync(ct);
        if (detail == default)
        {
            return ApiError.NotFound("Service not found.");
        }

        // Readers for each sub-query are closed before the next is issued.
        List<ServiceProvisioner> provisioners = await conn.GetServiceProvisionersAsync(id, ct)
            .Select(p => new ServiceProvisioner(p.Provisioner, p.ProvisionerType, p.DefaultDuration))
            .ToListAsync(ct);

        List<ServiceZone> zones = await conn.GetServiceZonesAsync(id, ct)
            .Select(z => new ServiceZone(z.Zone, z.ZoneType))
            .ToListAsync(ct);

        List<ServiceScope> scopes = await conn.GetServiceScopesAsync(id, ct)
            .Select(s => new ServiceScope(s.Scope, s.Enabled, s.StartAddress, s.EndAddress, s.SubnetMask, s.Gateway))
            .ToListAsync(ct);

        ServiceDetailResponse response = new(
            Service: detail.Service,
            ServiceId: detail.ServiceId,
            Type: detail.Type,
            DeviceId: detail.DeviceId,
            CaStatus: detail.CaStatus,
            CaAddress: detail.CaAddress,
            RootSubjectDn: detail.RootSubjectDn,
            RootNotBefore: detail.RootNotBefore?.UtcDateTime,
            RootNotAfter: detail.RootNotAfter?.UtcDateTime,
            RootFingerprint: detail.RootFingerprint,
            IntSubjectDn: detail.IntSubjectDn,
            IntNotBefore: detail.IntNotBefore?.UtcDateTime,
            IntNotAfter: detail.IntNotAfter?.UtcDateTime,
            TotalQueries: detail.TotalQueries,
            TotalBlocked: detail.TotalBlocked,
            BlockedPct: detail.BlockedPct,
            Provisioners: provisioners,
            Zones: zones,
            Scopes: scopes
        );

        return Results.Ok(response);
    }
}

public sealed record ServiceListItem(
    string Service,
    string? Type,
    string? DeviceId,
    string? CaStatus,
    DateTime? RootNotAfter,
    long? TotalQueries,
    double? BlockedPct
);

public sealed record ServiceProvisioner(string Provisioner, string? Type, string? DefaultDuration);

public sealed record ServiceZone(string Zone, string? Type);

public sealed record ServiceDnsRecord(string Zone, string Record, string Type, string? Value, int? Ttl);

public sealed record ServiceScope(
    string Scope,
    bool? Enabled,
    string? StartAddress,
    string? EndAddress,
    string? SubnetMask,
    string? Gateway
);

public sealed record ServiceDetailResponse(
    string Service,
    string? ServiceId,
    string? Type,
    string? DeviceId,
    string? CaStatus,
    string? CaAddress,
    string? RootSubjectDn,
    DateTime? RootNotBefore,
    DateTime? RootNotAfter,
    string? RootFingerprint,
    string? IntSubjectDn,
    DateTime? IntNotBefore,
    DateTime? IntNotAfter,
    long? TotalQueries,
    long? TotalBlocked,
    double? BlockedPct,
    IReadOnlyList<ServiceProvisioner> Provisioners,
    IReadOnlyList<ServiceZone> Zones,
    IReadOnlyList<ServiceScope> Scopes
);