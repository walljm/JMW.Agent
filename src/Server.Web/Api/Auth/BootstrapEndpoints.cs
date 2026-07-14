namespace JMW.Discovery.Server.Auth;

/// <summary>
/// Bootstrap endpoints — GET /Bootstrap is handled by Pages/Bootstrap.cshtml.
/// POST /bootstrap (form submit) is handled by Pages/Bootstrap.cshtml.cs OnPostAsync.
/// This class is kept as a registration point for any future bootstrap-related
/// minimal API endpoints (e.g. status checks).
/// </summary>
public static class BootstrapEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // No minimal API routes needed — Razor Page handles both GET and POST.
    }
}