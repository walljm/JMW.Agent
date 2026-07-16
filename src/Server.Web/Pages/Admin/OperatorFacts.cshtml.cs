using System.Text.Json;

using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.ManualFacts;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JMW.Discovery.Server.Pages.Admin;

/// <summary>
/// Fleet-wide operator-facts browse (docs/plans/architecture-operator-facts.md, SCR-002). Answers
/// "which devices have an operator-authored value for fact X" and "what operator-authored facts
/// exist at all." The page is client-driven — results and pagination come from the admin
/// operator-facts endpoints; this model only supplies the CSRF token and the combo catalog.
/// </summary>
[Authorize(Policy = RbacPolicies.Admin)]
public sealed class OperatorFactsModel : PageModel
{
    private readonly IAntiforgery _antiforgery;

    public OperatorFactsModel(IAntiforgery antiforgery)
    {
        _antiforgery = antiforgery;
    }

    public string AntiforgeryToken { get; private set; } = string.Empty;

    /// <summary>The overridable catalog serialized for the fact-path combo box (read client-side).</summary>
    public string CatalogJson => JsonSerializer.Serialize(OperatorFactCatalog.OverridablePaths);

    public void OnGet()
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        AntiforgeryToken = tokens.RequestToken ?? string.Empty;
    }
}
