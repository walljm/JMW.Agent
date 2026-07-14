using Microsoft.AspNetCore.Razor.TagHelpers;

namespace JMW.Discovery.Server.UI;

/// <summary>
/// Renders a data-grid column header as sortable: <c>aria-sort</c> plus an htmx-driven
/// <c>hx-get</c>/<c>hx-target</c>/<c>hx-swap</c> that swaps in the sorted fragment — replacing the
/// full-page-reload sort links tables used to hand-roll. A column that isn't in the page's sort
/// allowlist (<see cref="GridState.IsSortable" />) renders as a plain, non-interactive header
/// instead of guessing. (Attributes can't be named <c>data-*</c> — ASP.NET Core reserves that
/// prefix and refuses to bind a tag helper property to it.)
///
/// Usage: <c>&lt;th sort-key="hostname" sort-grid="Model.Grid" sort-target="#hosts-table"&gt;
/// Hostname&lt;/th&gt;</c>. Non-sortable columns just omit <c>sort-key</c> — this tag helper never
/// fires for them.
/// </summary>
[HtmlTargetElement("th", Attributes = SortKeyAttribute)]
public sealed class SortableHeaderTagHelper : TagHelper
{
    private const string SortKeyAttribute = "sort-key";
    private const string GridAttribute = "sort-grid";
    private const string TargetAttribute = "sort-target";

    [HtmlAttributeName(SortKeyAttribute)]
    public string SortKey { get; set; } = "";

    [HtmlAttributeName(GridAttribute)]
    public GridState? Grid { get; set; }

    [HtmlAttributeName(TargetAttribute)]
    public string HtmxTarget { get; set; } = "";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll(SortKeyAttribute);
        output.Attributes.RemoveAll(GridAttribute);
        output.Attributes.RemoveAll(TargetAttribute);

        if (Grid is null || !Grid.IsSortable(SortKey))
        {
            output.Attributes.SetAttribute("style", "cursor:default");
            return;
        }

        output.Attributes.SetAttribute("aria-sort", Grid.SortAria(SortKey));
        output.Attributes.SetAttribute("style", "cursor:pointer");
        output.Attributes.SetAttribute("hx-get", Grid.SortFragmentHref(SortKey));
        output.Attributes.SetAttribute("hx-push-url", Grid.SortHref(SortKey));
        if (!string.IsNullOrEmpty(HtmxTarget))
        {
            output.Attributes.SetAttribute("hx-target", HtmxTarget);
            output.Attributes.SetAttribute("hx-swap", "outerHTML");
        }
    }
}