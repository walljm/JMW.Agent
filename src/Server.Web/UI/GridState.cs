namespace JMW.Discovery.Server.UI;

/// <summary>
/// Shared sort + filter + pagination-href state for a keyset-paginated report grid. Replaces the
/// local <c>@functions</c> block (<c>SortHref</c>/<c>SortAria</c>/<c>FilterParams</c>/
/// <c>BuildPaginationLinks</c>) that used to be copy-pasted per table partial. Build one via
/// <see cref="GridModelBuilder.Build" /> rather than constructing directly.
/// </summary>
public sealed class GridState
{
    private readonly string _pageUrl;
    private readonly string _defaultSort;
    private readonly string _defaultDir;
    private readonly IReadOnlySet<string> _sortableColumns;
    private readonly IReadOnlyDictionary<string, string> _filterParams;
    private readonly string? _q;

    public GridState(
        string pageUrl,
        string defaultSort,
        string? sort,
        string? dir,
        IReadOnlySet<string> sortableColumns,
        IReadOnlyDictionary<string, string> filterParams,
        string? q,
        string defaultDir = "asc"
    )
    {
        _pageUrl = pageUrl;
        _defaultSort = defaultSort;
        _defaultDir = string.Equals(defaultDir, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
        _sortableColumns = sortableColumns;
        _filterParams = filterParams;
        _q = q;

        ActiveSort = sort is not null && sortableColumns.Contains(sort) ? sort : defaultSort;
        ActiveDir = string.IsNullOrEmpty(dir)
            ? _defaultDir
            : string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
    }

    /// <summary>The active sort column, resolved to the page's default when unset/invalid.</summary>
    public string ActiveSort { get; }

    /// <summary>
    /// The active direction ("asc"/"desc"). Falls back to the page's <c>defaultDir</c> (not
    /// always ascending) when the URL carries no <c>dir</c> — e.g. a "newest first" list like
    /// Agents defaults to descending.
    /// </summary>
    public string ActiveDir { get; }

    /// <summary>The page's natural default direction, passed through so callers building
    /// "is this the default view" checks don't have to hardcode "asc".</summary>
    public string DefaultDir => _defaultDir;

    public bool IsSortable(string column) => _sortableColumns.Contains(column);

    /// <summary>A full-page-reload sort link for column <paramref name="column" />: sets it as the
    /// sort column and toggles direction (asc→desc) when it's already active, else ascending. Drops
    /// any <c>after</c> cursor so sorting restarts at page 1.</summary>
    public string SortHref(string column) => BuildSortUrl(column, fragment: false);

    /// <summary>Same as <see cref="SortHref" /> but for the htmx fragment endpoint (used as the
    /// sortable header's <c>hx-get</c> target).</summary>
    public string SortFragmentHref(string column) => BuildSortUrl(column, fragment: true);

    /// <summary>aria-sort value for the active column ("ascending"/"descending"); "" otherwise.</summary>
    public string SortAria(string column)
    {
        if (!string.Equals(ActiveSort, column, StringComparison.Ordinal))
        {
            return "";
        }

        return ActiveDir == "asc" ? "ascending" : "descending";
    }

    /// <summary>Active filters + <c>q</c>, as a query-string fragment with no leading separator.</summary>
    public string FilterQuery()
    {
        List<string> parts = [];
        foreach ((string key, string value) in _filterParams)
        {
            parts.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
        }

        if (!string.IsNullOrEmpty(_q))
        {
            parts.Add("q=" + Uri.EscapeDataString(_q));
        }

        return string.Join('&', parts);
    }

    /// <summary>Filters + the active sort (when non-default) — used by pagination links so
    /// Next/First keep the current order.</summary>
    public string SortAndFilterQuery()
    {
        string filters = FilterQuery();
        bool nonDefault = !string.Equals(ActiveSort, _defaultSort, StringComparison.Ordinal) || ActiveDir != _defaultDir;
        if (!nonDefault)
        {
            return filters;
        }

        string sort = "sort=" + ActiveSort + "&dir=" + ActiveDir;
        return filters.Length > 0 ? sort + "&" + filters : sort;
    }

    public PaginationLinks BuildPaginationLinks(string? after, string? nextCursor)
    {
        string filterQuery = SortAndFilterQuery();
        string? firstHref = string.IsNullOrEmpty(after)
            ? null
            : _pageUrl + (filterQuery.Length > 0 ? "?" + filterQuery : "");
        string? nextHref = string.IsNullOrEmpty(nextCursor)
            ? null
            : _pageUrl + "?after=" + Uri.EscapeDataString(nextCursor) + (filterQuery.Length > 0 ? "&" + filterQuery : "");
        return new PaginationLinks(firstHref, nextHref);
    }

    private string BuildSortUrl(string column, bool fragment)
    {
        bool active = string.Equals(ActiveSort, column, StringComparison.Ordinal);
        string nextDir = active && ActiveDir == "asc" ? "desc" : "asc";
        string filters = FilterQuery();
        string url = _pageUrl + "?sort=" + column + "&dir=" + nextDir;
        if (filters.Length > 0)
        {
            url += "&" + filters;
        }

        return fragment ? url + "&fragment=1" : url;
    }
}