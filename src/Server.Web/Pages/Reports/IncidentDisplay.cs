namespace JMW.Discovery.Server.Pages.Reports;

/// <summary>
/// Presentation metadata for each incident/event type — label text, severity tier, and (if any)
/// the report page a "Needs Attention" row links to. Kept separate from IncidentTypeRegistry
/// (Ingest/Incidents/) deliberately: that registry defines WHEN an incident opens/resolves; this
/// one only says how to talk about it, so a new incident type appearing on the Dashboard/Recent
/// Activity/Device History needs an entry here, not new markup in any of those three views.
/// </summary>
public sealed record IncidentDisplayInfo(string Label, string Severity, string? Href, bool AdminOnly = false);

public static class IncidentDisplay
{
    public static readonly IReadOnlyDictionary<string, IncidentDisplayInfo> Types =
        new Dictionary<string, IncidentDisplayInfo>(StringComparer.Ordinal)
        {
            ["smart_failing"] = new("disks failing SMART health", "crit", "/storage"),
            ["filesystem_full"] = new("filesystems over 90% full", "warn", "/storage"),
            ["container_not_running"] = new("containers not running (crashed / stopped)", "warn", "/containers"),
            ["fingerprint_conflict"] =
                new("device records sharing a fingerprint — resolve by merge", "warn", "/admin/conflicts", AdminOnly: true),
            ["agent_offline"] = new("agents offline", "warn", "/fleet/agents"),
            ["cert_expiring"] = new("service CA certificates expiring soon", "info", "/terrain/ca-services"),
            ["service_down"] = new("services down or not reporting", "warn", "/services"),
            // No dedicated report page yet — matches the prior "Detailed view coming soon" info row.
            ["hardware_failed"] = new("failed hardware components", "info", null),
        };

    public static readonly IReadOnlyDictionary<string, string> EventLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["discovered"] = "discovered",
            ["promoted"] = "promoted to managed",
            ["merged"] = "merged",
        };

    /// <summary>Narrative label for a Recent Activity / Device History row of either kind.</summary>
    public static string Label(string kind, string typeName) =>
        kind == "event"
            ? EventLabels.TryGetValue(typeName, out string? evt) ? evt : typeName
            : Types.TryGetValue(typeName, out IncidentDisplayInfo? info) ? info.Label : typeName;
}