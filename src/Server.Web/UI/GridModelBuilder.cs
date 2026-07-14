namespace JMW.Discovery.Server.UI;

/// <summary>Everything a report page's Razor view needs to render its filter bar, sortable
/// headers, and pagination — built in one call from the page model's bound state.</summary>
public sealed record GridModel(GridState Grid, DataGridModel FilterBar, PaginationLinks Pagination);

/// <summary>
/// Builds the three pieces a data-grid page's view needs (<see cref="GridState" /> for sortable
/// headers, <see cref="DataGridModel" /> for the filter bar, <see cref="PaginationLinks" /> for
/// First/Next) from a page model's bound properties. Centralizes the <c>sortSuffix</c>-into-
/// <c>FragmentUrl</c> logic that used to be hand-rolled per page (e.g. AllHosts.cshtml).
/// </summary>
public static class GridModelBuilder
{
    public static GridModel Build(
        string pageUrl,
        IReadOnlyList<FilterSpec> filterSpecs,
        IReadOnlyDictionary<string, string> activeFilters,
        string? q,
        string defaultSort,
        string? sort,
        string? dir,
        IReadOnlySet<string> sortableColumns,
        string? afterCursor,
        string? nextCursor,
        string htmxTarget,
        string defaultDir = "asc"
    )
    {
        GridState grid = new(pageUrl, defaultSort, sort, dir, sortableColumns, activeFilters, q, defaultDir);

        bool sortIsDefault = string.Equals(grid.ActiveSort, defaultSort, StringComparison.Ordinal)
            && grid.ActiveDir == grid.DefaultDir;
        string sortSuffix = sortIsDefault ? "" : $"&sort={grid.ActiveSort}&dir={grid.ActiveDir}";

        DataGridModel filterBar = new()
        {
            Filters = filterSpecs,
            ActiveFilters = activeFilters,
            Q = q ?? "",
            NextCursor = nextCursor,
            FragmentUrl = pageUrl + "?fragment=1" + sortSuffix,
            HtmxTarget = htmxTarget,
            PageUrl = pageUrl,
        };

        PaginationLinks pagination = grid.BuildPaginationLinks(afterCursor, nextCursor);

        return new GridModel(grid, filterBar, pagination);
    }
}