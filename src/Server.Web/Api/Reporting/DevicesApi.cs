using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.FactViews;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class DevicesApi
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    private static readonly HashSet<string> ValidProtocols =
        new(StringComparer.Ordinal)
        {
            "ssh",
            "snmp",
            "http",
            "cert",
            "bacnet",
            "modbus",
        };

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/devices", ListDevices)
            .RequireAuthorization(ReadPolicy.Name);

        app.MapGet("/devices/{id}", GetDevice)
            .RequireAuthorization(ReadPolicy.Name);

        app.MapGet("/devices/{id}/facts-by-source", GetDeviceFactsBySource)
            .RequireAuthorization(ReadPolicy.Name);

        app.MapPost("/devices/{id}/merge", MergeDevice)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapDelete("/devices/{id}", DeleteDevice)
            .RequireAuthorization(RbacPolicies.Admin);

        app.MapPost("/devices/{id}/promote", PromoteDevice)
            .RequireAuthorization(RbacPolicies.Admin);
    }

    private static async Task<IResult> ListDevices(
        NpgsqlDataSource db,
        string? after,
        string? status,
        string? q,
        int limit = DefaultLimit,
        CancellationToken ct = default
    )
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        string? statusFilter = StatusFilter.NormalizeStatus(status);

        string? afterHostname = null;
        string? afterDeviceId = null;
        if (!string.IsNullOrEmpty(after))
        {
            if (!KeysetCursor.TryDecode(after, out string hostname, out string deviceId))
            {
                return ApiError.InvalidCursor();
            }

            afterHostname = hostname;
            afterDeviceId = deviceId;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<(Guid DeviceId, string? Hostname, string? OsFamily, string? OsDistro, string ManagementStatus,
            DateTimeOffset? LastSeen, string? Vendor)> rows = await conn.ListDevicesAsync(
                statusFilter,
                afterHostname,
                afterDeviceId,
                string.IsNullOrWhiteSpace(q) ? null : q,
                limit + 1,
                ct
            )
            .ToListAsync(ct);

        string? nextCursor = KeysetPage.NextCursor(
            rows,
            limit,
            r => [r.Hostname ?? string.Empty, r.DeviceId.ToString()]
        );

        List<DeviceListItem> items = rows.Select(r => new DeviceListItem(
                    DeviceId: r.DeviceId.ToString(),
                    Hostname: r.Hostname,
                    OsFamily: r.OsFamily,
                    OsDistro: r.OsDistro,
                    ManagementStatus: r.ManagementStatus,
                    LastSeen: r.LastSeen?.UtcDateTime,
                    Vendor: r.Vendor
                )
            )
            .ToList();

        return Results.Ok(
            new
            {
                items,
                next_cursor = nextCursor,
            }
        );
    }

    private static async Task<IResult> GetDevice(
        string id,
        NpgsqlDataSource db,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid deviceId))
        {
            return ApiError.NotFound("Device not found.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        (Guid DeviceId, string ManagementStatus, string? Hostname, string? FriendlyName, string? OsFamily, string?
            OsDistro,
            string? OsDistroGuess, DateTimeOffset? LastSeen, string? Vendor, string?
            VendorSourceName,
            string? Kind, string? CpuModel, long? CpuCores, long? TotalMemBytes, string? SystemVendor, string?
            SystemModel, string? SystemSerial, string? LastSeenIp)
            summary = await conn.GetDeviceSummaryAsync(deviceId, ct).FirstOrDefaultAsync(ct);
        if (summary == default)
        {
            return ApiError.NotFound("Device not found.");
        }

        // Reader for the summary query is closed before issuing the fingerprint query.
        List<DeviceFingerprint> fingerprints = await conn.GetDeviceFingerprintsAsync(deviceId, ct)
            .Select(f => new DeviceFingerprint(f.FpType, f.FpValue, f.Source, f.LastSeen.UtcDateTime))
            .ToListAsync(ct);

        DeviceDetailResponse detail = new(
            DeviceId: summary.DeviceId.ToString(),
            ManagementStatus: summary.ManagementStatus,
            Hostname: summary.Hostname,
            FriendlyName: summary.FriendlyName,
            OsFamily: summary.OsFamily,
            OsDistro: summary.OsDistro ?? summary.OsDistroGuess,
            LastSeen: summary.LastSeen?.UtcDateTime,
            Vendor: summary.Vendor,
            Kind: summary.Kind,
            CpuModel: summary.CpuModel,
            CpuCores: summary.CpuCores,
            TotalMemBytes: summary.TotalMemBytes,
            SystemVendor: summary.SystemVendor,
            SystemModel: summary.SystemModel,
            SystemSerial: summary.SystemSerial,
            LastSeenIp: summary.LastSeenIp,
            Fingerprints: fingerprints
        );

        return Results.Ok(detail);
    }

    /// <summary>
    /// A collector's "blast radius" on this device — its current facts (latest value per fact
    /// id) whose latest write came from the given collector. Drives the Activity tab's
    /// "which facts does this failing collector own" drill-down (F4).
    /// </summary>
    private static async Task<IResult> GetDeviceFactsBySource(
        string id,
        string source_name,
        NpgsqlDataSource db,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid deviceId))
        {
            return ApiError.NotFound("Device not found.");
        }

        if (string.IsNullOrWhiteSpace(source_name))
        {
            return ApiError.InvalidRequest("source_name is required.");
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);
        List<(string AttributePath, string? KeyValues, string? Value, DateTimeOffset CollectedAt)> rows =
            await conn.GetDeviceFactsBySourceAsync(deviceId, source_name, ct).ToListAsync(ct);

        object[] facts = rows
            .Select(r => (object)new
            {
                attribute_path = r.AttributePath,
                key = FactViewRenderer.ExtractRowKey(r.KeyValues, "Device"),
                value = r.Value,
                collected_at = r.CollectedAt.UtcDateTime,
            })
            .ToArray();

        return Results.Ok(new { facts });
    }

    private static async Task<IResult> MergeDevice(
        string id,
        MergeRequest body,
        DeviceRegistry registry,
        HttpContext context,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out _))
        {
            return ApiError.InvalidId("Invalid device id.");
        }

        if (!Guid.TryParse(body.IntoDeviceId, out _))
        {
            return ApiError.InvalidId("Invalid into_device_id.");
        }

        if (string.Equals(id, body.IntoDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return ApiError.InvalidRequest("Cannot merge a device into itself.");
        }

        string actor = context.User.Identity?.Name ?? "unknown";

        try
        {
            await registry.ManualMergeAsync(loserId: id, survivorId: body.IntoDeviceId, actor: actor, ct: ct);
        }
        catch (DeviceMergeConflictException ex)
        {
            return ApiError.Conflict(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return ApiError.NotFound(ex.Message);
        }

        return Results.Ok(
            new
            {
                survivor_device_id = body.IntoDeviceId,
            }
        );
    }

    /// <summary>
    /// Manual fallback for a bad auto-merge (or any device an operator wants gone) — hard-deletes
    /// the device and everything associated with it (fingerprints, projections, history, incidents,
    /// change events). Does not attempt a re-split: there's nothing to reconstruct a merge from
    /// (see DeviceRegistry.DeleteDeviceAsync). The next time this physical entity is observed it
    /// resolves as a brand-new device rather than joining stale state.
    /// </summary>
    private static async Task<IResult> DeleteDevice(
        string id,
        DeviceRegistry registry,
        HttpContext context,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out _))
        {
            return ApiError.InvalidId("Invalid device id.");
        }

        string actor = context.User.Identity?.Name ?? "unknown";

        try
        {
            await registry.DeleteDeviceAsync(id, actor, ct);
        }
        catch (ArgumentException ex)
        {
            return ApiError.NotFound(ex.Message);
        }

        return Results.Ok(new { deleted_device_id = id });
    }

    private static async Task<IResult> PromoteDevice(
        string id,
        PromoteRequest body,
        NpgsqlDataSource db,
        AuditLog audit,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(id, out Guid _))
        {
            return ApiError.InvalidId("Invalid device id.");
        }

        if (!Guid.TryParse(body.AgentId, out Guid agentId))
        {
            return ApiError.InvalidId("Invalid agent_id.");
        }

        if (string.IsNullOrWhiteSpace(body.TargetAddress))
        {
            return ApiError.InvalidRequest("target_address is required.");
        }

        if (!ValidProtocols.Contains(body.Protocol ?? ""))
        {
            return ApiError.InvalidRequest($"protocol must be one of: {string.Join(", ", ValidProtocols)}.");
        }

        Guid? credentialId = null;
        if (!string.IsNullOrEmpty(body.CredentialId))
        {
            if (!Guid.TryParse(body.CredentialId, out Guid parsedCred))
            {
                return ApiError.InvalidId("Invalid credential_id.");
            }

            credentialId = parsedCred;
        }

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        // Verify device exists.
        await using (NpgsqlCommand checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT 1 FROM devices WHERE device_id = $1::uuid LIMIT 1";
            checkCmd.Parameters.Add(Param.Text(id));
            if (await checkCmd.ExecuteScalarAsync(ct) is null)
            {
                return ApiError.NotFound("Device not found.");
            }
        }

        // Create the collection target.
        TargetIdResult targetRow = await conn
            .InsertTargetAsync(agentId, body.TargetAddress, body.Protocol!, credentialId, null, ct)
            .FirstOrDefaultAsync(ct);

        // Flip management_status to managed.
        await using (NpgsqlCommand promoteCmd = conn.CreateCommand())
        {
            promoteCmd.CommandText = """
                UPDATE devices SET management_status = 'managed', updated_at = now()
                WHERE device_id = $1::uuid
                """;
            promoteCmd.Parameters.Add(Param.Text(id));
            await promoteCmd.ExecuteNonQueryAsync(ct);
        }

        await audit.WriteAsync(
            actor: "admin",
            action: "device.promote",
            targetRef: id,
            detail: new
            {
                device_id = id,
                agent_id = body.AgentId,
                target_id = targetRow.TargetId,
                target_address = body.TargetAddress,
                protocol = body.Protocol,
            },
            ct: ct
        );

        return Results.Ok(
            new
            {
                device_id = id,
                target_id = targetRow.TargetId,
            }
        );
    }
}

public sealed record MergeRequest(string IntoDeviceId);

public sealed record PromoteRequest(string AgentId, string? CredentialId, string TargetAddress, string? Protocol);

public sealed record DeviceListItem(
    string DeviceId,
    string? Hostname,
    string? OsFamily,
    string? OsDistro,
    string ManagementStatus,
    DateTime? LastSeen,
    string? Vendor
);

public sealed record DeviceFingerprint(
    string Type,
    string Value,
    string? Source,
    DateTime LastSeen
);

public sealed record DeviceDetailResponse(
    string DeviceId,
    string ManagementStatus,
    string? Hostname,
    string? FriendlyName,
    string? OsFamily,
    string? OsDistro,
    DateTime? LastSeen,
    string? Vendor,
    string? Kind,
    string? CpuModel,
    long? CpuCores,
    long? TotalMemBytes,
    string? SystemVendor,
    string? SystemModel,
    string? SystemSerial,
    string? LastSeenIp,
    IReadOnlyList<DeviceFingerprint> Fingerprints
);