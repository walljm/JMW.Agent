using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

namespace JMW.Discovery.Server.Auth;

public static class RbacPolicies
{
    public const string Admin = "RequireAdmin";
    public const string Authenticated = "RequireAuthenticated";

    public static readonly Action<AuthorizationPolicyBuilder> AdminPolicy =
        policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ClaimTypes.Role, "admin");

    public static readonly Action<AuthorizationPolicyBuilder> AuthenticatedPolicy =
        policy => policy.RequireAuthenticatedUser();
}