using System.Globalization;
using System.Text;
using System.Text.Json;

using JMW.Discovery.Core;
using JMW.Discovery.Server.Api;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Reporting;

public static class ChangesApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/report/changes", ListChanges)
            .RequireAuthorization(ReadPolicy.Name);
    }

    private static async Task<IResult> ListChanges(
        NpgsqlDataSource db,
        string? after,
        string? since,
        string? device_id,
        int limit = DefaultLimit,
        CancellationToken ct = default
    )
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        DateTimeOffset? afterCollectedAt = null;
        string? afterId = null;
        if (!string.IsNullOrEmpty(after))
        {
            if (!KeysetCursor.TryDecodeParts(after, 2, out string[] parts)
             || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
            {
                return ApiError.InvalidCursor();
            }

            afterCollectedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            afterId = parts[1];
        }

        (List<ChangeListItem> items, string? nextCursor) = await QueryAsync(
            db,
            since,
            device_id,
            afterCollectedAt,
            afterId,
            limit,
            ct
        );

        return Results.Ok(
            new
            {
                items,
                next_cursor = nextCursor,
            }
        );
    }

    public static async Task<(List<ChangeListItem> Items, string? NextCursor)> QueryAsync(
        NpgsqlDataSource db,
        string? since,
        string? deviceId,
        DateTimeOffset? afterCollectedAt,
        string? afterId,
        int limit,
        CancellationToken ct
    )
    {
        DateTimeOffset? sinceTs = ResolveSince(since);

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(ct);

        List<(string Id, string AttributePath, string? KeyValues, short Kind, string? ValueStr, long? ValueLong, double?
            ValueDouble, DateTimeOffset CollectedAt, string? Hostname, string? FriendlyName)> rows = await conn.ListChangesAsync(
                sinceTs,
                string.IsNullOrWhiteSpace(deviceId) ? null : deviceId,
                afterCollectedAt,
                afterId,
                limit + 1,
                ct
            )
            .ToListAsync(ct);

        string? nextCursor = KeysetPage.NextCursor(
            rows,
            limit,
            r => [r.CollectedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), r.Id]
        );

        List<ChangeListItem> items = rows.Select(r => new ChangeListItem(
                    Id: r.Id,
                    AttributePath: r.AttributePath,
                    KeyValues: r.KeyValues,
                    Kind: r.Kind,
                    Value: RenderValue(r.Kind, r.ValueStr, r.ValueLong, r.ValueDouble),
                    CollectedAt: r.CollectedAt.UtcDateTime,
                    Hostname: r.Hostname,
                    FriendlyName: r.FriendlyName
                )
            )
            .ToList();

        return (items, nextCursor);
    }

    /// <summary>Translates a relative window token to an absolute lower bound.</summary>
    public static DateTimeOffset? ResolveSince(string? since) =>
        since switch
        {
            "1h" => DateTimeOffset.UtcNow.AddHours(-1),
            "6h" => DateTimeOffset.UtcNow.AddHours(-6),
            "24h" => DateTimeOffset.UtcNow.AddHours(-24),
            "7d" => DateTimeOffset.UtcNow.AddDays(-7),
            _ => null,
        };

    /// <summary>
    /// Renders the change value according to its fact kind, mirroring how the
    /// ingest pipeline (FactRepository) maps each <see cref="FactValueKind" /> onto
    /// the value_str / value_long / value_double columns:
    /// String, IP*, Mac    → value_str (human-readable, render as-is)
    /// Long                → value_long (integer)
    /// Double              → value_double
    /// Bool                → value_long as 1/0 → true/false
    /// DateTimeOffset      → value_long as UTC ticks → ISO timestamp
    /// TimeSpan            → value_long as ticks → duration
    /// </summary>
    public static string RenderValue(short kind, string? valueStr, long? valueLong, double? valueDouble)
    {
        switch ((FactValueKind)kind)
        {
            case FactValueKind.Double:
                return valueDouble?.ToString("0.######", CultureInfo.InvariantCulture) ?? "—";

            case FactValueKind.Bool:
                return valueLong is null ? "—" : valueLong.Value != 0 ? "true" : "false";

            case FactValueKind.DateTimeOffset:
                return valueLong is null
                    ? "—"
                    : new DateTimeOffset(valueLong.Value, TimeSpan.Zero)
                        .UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                  + " UTC";

            case FactValueKind.TimeSpan:
                return valueLong is null
                    ? "—"
                    : TimeSpan.FromTicks(valueLong.Value).ToString();

            case FactValueKind.Long:
                return valueLong?.ToString(CultureInfo.InvariantCulture) ?? "—";

            default:
                // String, IPv4/IPv6/IPPrefix, MacAddress, Null — all land in value_str.
                if (valueStr is not null)
                {
                    return valueStr;
                }

                if (valueLong is not null)
                {
                    return valueLong.Value.ToString(CultureInfo.InvariantCulture);
                }

                return valueDouble?.ToString("0.######", CultureInfo.InvariantCulture) ?? "—";
        }
    }
}

public sealed record ChangeListItem(
    string Id,
    string AttributePath,
    string? KeyValues,
    short Kind,
    string Value,
    DateTime CollectedAt,
    string? Hostname,
    string? FriendlyName
)
{
    /// <summary>
    /// Reconstructs the full fact ID by filling the [] brackets in AttributePath
    /// with the actual key values from KeyValues JSON.
    /// e.g. "Device[].Interface[].Speed" + {"Device":"r1","Interface":"eth0"}
    /// => "Device[r1].Interface[eth0].Speed"
    /// </summary>
    public string FullPath
    {
        get
        {
            if (string.IsNullOrEmpty(KeyValues) || KeyValues == "{}")
            {
                return AttributePath;
            }

            using JsonDocument doc = JsonDocument.Parse(KeyValues);
            string[] values = doc.RootElement.EnumerateObject()
                .Select(p => p.Value.GetString() ?? string.Empty)
                .ToArray();

            if (values.Length == 0)
            {
                return AttributePath;
            }

            StringBuilder sb = new(AttributePath.Length + values.Sum(v => v.Length));
            int ki = 0, pos = 0;
            while (pos < AttributePath.Length)
            {
                int bracket = AttributePath.IndexOf("[]", pos, StringComparison.Ordinal);
                if (bracket < 0 || ki >= values.Length)
                {
                    sb.Append(AttributePath, pos, AttributePath.Length - pos);
                    break;
                }

                sb.Append(AttributePath, pos, bracket - pos);
                sb.Append('[');
                sb.Append(values[ki++]);
                sb.Append(']');
                pos = bracket + 2;
            }

            return sb.ToString();
        }
    }
}