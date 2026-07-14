using System.Text;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Razor.TagHelpers;

namespace JMW.Discovery.Server.UI;

/// <summary>
/// Renders the dashboard panel shell — <c>&lt;div class="dash-panel" hx-get hx-trigger
/// hx-swap&gt;</c> + <c>panel-head</c> + title + optional action link/meta text — that was
/// copy-pasted across the dashboard's panel partials (review D18). Only wraps the shell; each
/// panel's body (the <c>err is not null ? _SectionError : ...</c> branch) stays as child content,
/// since that part is genuinely panel-specific, not boilerplate.
///
/// Usage:
/// <code>
/// &lt;dash-panel panel-name="changes" title="Recent changes" refresh-seconds="30"
///             action-href="/changes" action-text="Change feed →"&gt;
///     @if (err is not null) { ... } else { ... }
/// &lt;/dash-panel&gt;
/// </code>
///
/// Two panels were deliberately NOT converted to this: <c>_TotalsPanel</c> (a <c>stat-row</c>, not
/// a <c>dash-panel</c>, with its own layout) and <c>_AttentionPanel</c> (a <c>dash-section</c> with
/// conditional visibility and a <c>hero</c> variant) — both structurally different from the shared
/// shell, not just differently parameterized.
/// </summary>
[HtmlTargetElement("dash-panel")]
public sealed class DashPanelTagHelper : TagHelper
{
    /// <summary>The <c>?fragment=</c> query value this panel's HTMX auto-refresh requests.</summary>
    public string PanelName { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>HTMX <c>hx-trigger="every {N}s"</c> auto-refresh interval.</summary>
    public int RefreshSeconds { get; set; } = 60;

    /// <summary>When set (with <see cref="ActionText" />), renders a header-right link.</summary>
    public string? ActionHref { get; set; }

    public string? ActionText { get; set; }

    /// <summary>When set (and <see cref="ActionHref" /> is not), renders plain header-right text.</summary>
    public string? MetaText { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        TagHelperContent body = await output.GetChildContentAsync();

        output.TagName = "div";
        output.Attributes.SetAttribute("class", "dash-panel");
        output.Attributes.SetAttribute("hx-get", $"/dashboard?fragment={PanelName}");
        output.Attributes.SetAttribute("hx-trigger", $"every {RefreshSeconds}s");
        output.Attributes.SetAttribute("hx-swap", "outerHTML");

        StringBuilder head = new();
        head.Append("<div class=\"panel-head\"><h2>").Append(HtmlEncoder.Default.Encode(Title)).Append("</h2>");
        if (ActionHref is not null)
        {
            head.Append("<a class=\"panel-action\" href=\"")
                .Append(HtmlEncoder.Default.Encode(ActionHref))
                .Append("\">")
                .Append(HtmlEncoder.Default.Encode(ActionText ?? ""))
                .Append("</a>");
        }
        else if (MetaText is not null)
        {
            head.Append("<span class=\"meta\">").Append(HtmlEncoder.Default.Encode(MetaText)).Append("</span>");
        }

        head.Append("</div>");

        output.PreContent.SetHtmlContent(head.ToString());
        output.Content.SetHtmlContent(body);
    }
}