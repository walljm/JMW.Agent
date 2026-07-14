using System.Net;

using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Data;
using JMW.Discovery.Server.Reporting;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class SubnetDetailModel : PageModel
{
    private readonly ILogger<SubnetDetailModel> _logger;
    private readonly NpgsqlDataSource _db;

    public SubnetDetailModel(NpgsqlDataSource db, ILogger<SubnetDetailModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public SubnetDetail? Subnet { get; private set; }
    public string? LoadError { get; private set; }
    public bool SubnetNotFound { get; private set; }
    public IReadOnlyList<SubnetArpNeighbor> ArpNeighbors { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string network, int prefix, CancellationToken ct)
    {
        if (!IPAddress.TryParse(network, out IPAddress? addr))
        {
            SubnetNotFound = true;
            return Page();
        }

        IPNetwork ipNetwork;
        try
        {
            ipNetwork = new IPNetwork(addr, prefix);
        }
        catch (ArgumentOutOfRangeException)
        {
            SubnetNotFound = true;
            return Page();
        }

        try
        {
            Subnet = await SubnetsApi.GetDetailAsync(_db, ipNetwork, ct);
            SubnetNotFound = Subnet is null;

            if (Subnet is not null)
            {
                ArpNeighbors = await QueryArpNeighborsAsync(ipNetwork, ct);
            }
        }
        catch (NpgsqlException ex)
        {
            LoadError = "This section could not be loaded. Try refreshing in a moment.";
            SubnetDetailModelLog.LoadFailed(_logger, ex);
        }

        return Page();
    }

    // Scoped by real CIDR containment (inet <<=), not the old substring-match bridge to
    // the ARP page — that only worked for octet-aligned /8, /16, /24 prefixes.
    private async Task<List<SubnetArpNeighbor>> QueryArpNeighborsAsync(IPNetwork ipNetwork, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                a.device, s.hostname AS observer_hostname, a.arp AS ip, a.mac, a.iface, a.state,
                CASE WHEN df.device_id IS NULL THEN NULL ELSE d.device_id END AS resolved_device_id,
                rs.hostname AS resolved_hostname
            FROM proj_device_arp a
                LEFT JOIN proj_systems s ON s.device = a.device
                LEFT JOIN device_fingerprints df ON df.fp_type = 'mac' AND df.fp_value = a.mac
                LEFT JOIN devices d ON d.device_id = df.device_id
                LEFT JOIN proj_systems rs ON rs.device = d.device_id::text
            WHERE a.arp::inet <<= $1::inet
            ORDER BY a.arp::inet
            """;
        cmd.Parameters.Add(Param.Text(ipNetwork.ToString()));

        List<SubnetArpNeighbor> items = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(
                new SubnetArpNeighbor(
                    Device: reader.GetString(0),
                    ObserverHostname: reader.IsDBNull(1) ? null : reader.GetString(1),
                    Ip: reader.GetString(2),
                    Mac: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Iface: reader.IsDBNull(4) ? null : reader.GetString(4),
                    State: reader.IsDBNull(5) ? null : reader.GetString(5),
                    ResolvedDeviceId: reader.IsDBNull(6) ? null : reader.GetGuid(6).ToString(),
                    ResolvedHostname: reader.IsDBNull(7) ? null : reader.GetString(7)
                )
            );
        }

        return items;
    }
}

internal static partial class SubnetDetailModelLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Subnet detail page load failed.")]
    public static partial void LoadFailed(ILogger logger, Exception ex);
}

public sealed record SubnetArpNeighbor(
    string Device,
    string? ObserverHostname,
    string Ip,
    string? Mac,
    string? Iface,
    string? State,
    string? ResolvedDeviceId,
    string? ResolvedHostname
);