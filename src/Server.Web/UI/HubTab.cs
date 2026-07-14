namespace JMW.Discovery.Server.UI;

/// <summary>One tab in a hub page's shared tab bar (see `_HubTabs.cshtml`).</summary>
public sealed record HubTab(string Label, string Href);

/// <summary>Model for `_HubTabs.cshtml`: the tab set for a hub and which one is active.</summary>
public sealed record HubTabsModel(IReadOnlyList<HubTab> Tabs, string ActiveHref, string AriaLabel);

/// <summary>
/// Single source of truth for each hub's tab set, so the tab list/order/labels are defined once
/// rather than duplicated across every member page.
/// </summary>
public static class HubTabSets
{
    public static readonly IReadOnlyList<HubTab> Inventory =
    [
        new HubTab("System Specs", "/hardware"),
        new HubTab("Component Inventory", "/components"),
        new HubTab("Storage", "/storage"),
        new HubTab("Interfaces", "/interfaces"),
        new HubTab("Ports", "/ports"),
        new HubTab("Containers", "/containers"),
    ];

    public static readonly IReadOnlyList<HubTab> Network =
    [
        new HubTab("Subnets", "/subnets"),
        new HubTab("ARP Table", "/arp"),
        new HubTab("DHCP Leases", "/terrain/dhcp"),
        new HubTab("DNS Records", "/terrain/dns"),
        new HubTab("Certificate Authorities", "/terrain/ca"),
    ];

    public static readonly IReadOnlyList<HubTab> Fleet =
    [
        new HubTab("Overview", "/fleet"),
        new HubTab("Agents", "/fleet/agents"),
        new HubTab("Credentials", "/admin/credentials"),
    ];

    public static readonly IReadOnlyList<HubTab> System =
    [
        new HubTab("Users", "/admin/users"),
        new HubTab("Settings", "/admin/settings"),
        new HubTab("Audit Log", "/admin/audit-log"),
    ];

    public static readonly IReadOnlyList<HubTab> Data =
    [
        new HubTab("Conflicts", "/admin/conflicts"),
        new HubTab("OUI Database", "/admin/oui-database"),
        new HubTab("Custom Fields", "/admin/custom-fields"),
    ];
}
