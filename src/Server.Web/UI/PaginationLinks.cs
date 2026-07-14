namespace JMW.Discovery.Server.UI;

/// <summary>
/// The two links a keyset-paginated table needs: "First" (back to the unfiltered start) and
/// "Next" (the following page). A null href hides the corresponding link. Callers build the
/// hrefs themselves (base URL + filters + cursor vary per page); <c>_Pagination.cshtml</c> only
/// renders them, so the markup that was copy-pasted across every table partial lives once.
/// </summary>
public sealed record PaginationLinks(string? FirstHref, string? NextHref);