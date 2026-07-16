namespace JMW.Discovery.Server.Reporting;

/// <summary>
/// Validates a caller-supplied <c>management_status</c> filter value against the two real
/// statuses, was re-declared verbatim in `DeviceListApi` and `DevicesApi` (review D29). Any other
/// value (typo, unrelated query param) is treated as "no filter" rather than passed through to
/// the SQL <c>WHERE</c> clause.
/// </summary>
public static class StatusFilter
{
    public static string? NormalizeStatus(string? status) =>
        status switch
        {
            "managed" => "managed",
            "discovered" => "discovered",
            _ => null,
        };
}