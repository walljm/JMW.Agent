using JMW.Discovery.Server.Auth;

namespace JMW.Discovery.Server.Reporting;

/// <summary>
/// Authorization policy for read-only reporting endpoints.
/// Reporting requires only an authenticated user, which is exactly what
/// <see cref="RbacPolicies.Authenticated" /> already provides, so this aliases
/// that policy name rather than registering a second identical policy.
/// </summary>
public static class ReadPolicy
{
    public const string Name = RbacPolicies.Authenticated;
}