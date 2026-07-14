using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.FactViews;
using JMW.Discovery.Server.Queries;
using JMW.Discovery.Server.Reporting;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class ServiceDetailModel : PageModel
{
    private readonly NpgsqlDataSource _db;

    public ServiceDetailModel(NpgsqlDataSource db)
    {
        _db = db;
    }

    public string Service { get; private set; } = string.Empty;
    public string? ServiceId { get; private set; }
    public string? Type { get; private set; }
    public string? DeviceId { get; private set; }

    public string? CaStatus { get; private set; }
    public string? CaAddress { get; private set; }
    public string? RootSubjectDn { get; private set; }
    public DateTime? RootNotBefore { get; private set; }
    public DateTime? RootNotAfter { get; private set; }
    public string? RootFingerprint { get; private set; }
    public string? IntSubjectDn { get; private set; }
    public DateTime? IntNotBefore { get; private set; }
    public DateTime? IntNotAfter { get; private set; }

    public long? TotalQueries { get; private set; }
    public long? TotalBlocked { get; private set; }
    public double? BlockedPct { get; private set; }

    public IReadOnlyList<ServiceProvisioner> Provisioners { get; private set; } = [];
    public IReadOnlyList<ServiceZone> Zones { get; private set; } = [];
    public IReadOnlyList<ServiceScope> Scopes { get; private set; } = [];
    public IReadOnlyList<ServiceDnsRecord> Records { get; private set; } = [];
    public IReadOnlyList<RenderedFactView> FactViews { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        Service = id;

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        (string Service, string? ServiceId, string? Type, string? DeviceId, string? CaStatus, string? CaAddress, string?
            RootSubjectDn, DateTimeOffset? RootNotBefore, DateTimeOffset? RootNotAfter, string? RootFingerprint, string?
            IntSubjectDn, DateTimeOffset? IntNotBefore, DateTimeOffset? IntNotAfter, long? TotalQueries, long?
            TotalBlocked, double? BlockedPct) detail = await conn.GetServiceDetailAsync(id, ct).FirstOrDefaultAsync(ct);
        if (detail == default)
        {
            return NotFound();
        }

        ServiceId = detail.ServiceId;
        Type = detail.Type;
        DeviceId = detail.DeviceId;
        CaStatus = detail.CaStatus;
        CaAddress = detail.CaAddress;
        RootSubjectDn = detail.RootSubjectDn;
        RootNotBefore = detail.RootNotBefore?.UtcDateTime;
        RootNotAfter = detail.RootNotAfter?.UtcDateTime;
        RootFingerprint = detail.RootFingerprint;
        IntSubjectDn = detail.IntSubjectDn;
        IntNotBefore = detail.IntNotBefore?.UtcDateTime;
        IntNotAfter = detail.IntNotAfter?.UtcDateTime;
        TotalQueries = detail.TotalQueries;
        TotalBlocked = detail.TotalBlocked;
        BlockedPct = detail.BlockedPct;

        // Each reader is fully materialized before the next query runs.
        Provisioners = await conn.GetServiceProvisionersAsync(id, ct)
            .Select(p => new ServiceProvisioner(p.Provisioner, p.ProvisionerType, p.DefaultDuration))
            .ToListAsync(ct);

        Zones = await conn.GetServiceZonesAsync(id, ct)
            .Select(z => new ServiceZone(z.Zone, z.ZoneType))
            .ToListAsync(ct);

        Scopes = await conn.GetServiceScopesAsync(id, ct)
            .Select(s => new ServiceScope(s.Scope, s.Enabled, s.StartAddress, s.EndAddress, s.SubnetMask, s.Gateway))
            .ToListAsync(ct);

        Records = await conn.GetServiceRecordsAsync(id, ct)
            .Select(r => new ServiceDnsRecord(r.Zone, r.Record, r.Rtype, r.Value, r.Ttl))
            .ToListAsync(ct);

        List<FactViewFact> facts = await conn.GetServiceAllFactsAsync(id, ct)
            .Select(r => new FactViewFact(r.AttributePath, FactViewRenderer.ExtractRowKey(r.KeyValues, "Service"), r.Value))
            .ToListAsync(ct);
        FactViews = FactViewRenderer.Render(facts, FactViewLibrary.Service);

        return Page();
    }
}