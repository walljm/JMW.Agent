using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Reports;

/// <summary>
/// Base for report page models: the try/catch → LoadError → log → fragment-vs-full-page dance
/// was duplicated across ~7 page models (review D14), each setting <c>LoadError = ex.Message</c> —
/// leaking raw Npgsql text (table/column/constraint names) to the view, unlike the dashboard's
/// <c>Fail()</c> helper which already keeps the exception out of the response. Subclasses keep
/// their own domain-named list property and query call; call <see cref="RunReportLoadAsync" /> for
/// the standard error-safe load + response shape.
/// </summary>
public abstract class ReportPageModel : PageModel
{
    /// <summary>
    /// The generic, safe-to-display message shown for any report section load failure — never the
    /// raw exception text, per the layer-separation rule. Shared so pages with multiple independent
    /// load sections (e.g. Storage, Terrain) that can't fit <see cref="RunReportLoadAsync" />'s
    /// single-section shape still show identical, non-leaking error text.
    /// </summary>
    public const string SafeLoadErrorMessage = "This section could not be loaded. Try refreshing in a moment.";

    /// <summary>
    /// Set on a load failure. Always a generic, safe-to-display message — never the raw exception
    /// text (that goes to <c>logFailure</c> only), per the layer-separation rule.
    /// </summary>
    public string? LoadError { get; protected set; }

    /// <summary>
    /// Runs <paramref name="load" />, catching <see cref="NpgsqlException" /> into a safe
    /// <see cref="LoadError" /> plus <paramref name="logFailure" />, then returns the table
    /// partial (when the request carries <c>?fragment=1</c>, for AJAX pagination/filtering) or
    /// the full page. Pass <paramref name="partialName" /> as <see langword="null" /> for a page
    /// that doesn't support the fragment refresh — it always returns the full page then.
    /// </summary>
    protected async Task<IActionResult> RunReportLoadAsync(
        Func<Task> load,
        Action<Exception> logFailure,
        string? partialName
    )
    {
        try
        {
            await load();
        }
        catch (NpgsqlException ex)
        {
            LoadError = SafeLoadErrorMessage;
            logFailure(ex);
        }

        if (partialName is not null && string.Equals(Request.Query["fragment"], "1", StringComparison.Ordinal))
        {
            return Partial(partialName, this);
        }

        return Page();
    }
}