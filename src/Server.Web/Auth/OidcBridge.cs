using System.Security.Claims;
using System.Security.Cryptography;

using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Queries;

using Microsoft.AspNetCore.Authentication.OpenIdConnect;

using Npgsql;

namespace JMW.Discovery.Server.Auth;

/// <summary>
/// Bridges a successful OIDC login into this app's own session system (SessionMiddleware +
/// the jmw_session cookie) instead of letting the OIDC handler establish its own cookie
/// identity. The OIDC handler's configured SignInScheme is only a structural requirement of
/// AddOpenIdConnect — this runs before that sign-in completes and short-circuits it via
/// HandleResponse(), so no ASP.NET Core cookie identity is ever actually persisted.
///
/// Local identity is keyed on the verified email claim, not the OIDC "sub" claim. Email is
/// mutable (a provider could reassign an address after a user changes it) where sub is the
/// spec-guaranteed stable per-issuer identifier — a follow-up hardening step would add an
/// oidc_subject column and key on (issuer, sub) instead, falling back to email only for
/// first-time account linking. Not done here: it's a schema change beyond this pass's scope,
/// and requiring email_verified (checked below) closes the main practical attack (an
/// unverified, attacker-chosen email claiming an existing account).
/// </summary>
public static class OidcBridge
{
    public static async Task HandleTokenValidatedAsync(TokenValidatedContext context)
    {
        ClaimsPrincipal? principal = context.Principal;
        string? email = principal?.FindFirstValue("email") ?? principal?.FindFirstValue(ClaimTypes.Email);

        if (string.IsNullOrWhiteSpace(email))
        {
            context.HandleResponse();
            context.Response.Redirect("/Login?error=oidc_no_email");
            return;
        }

        // The email claim is the account-linking key (see the class doc-comment caveat below on
        // why that's not fully immutable-identity-safe) — an unverified email is attacker-settable
        // at some providers, which would let one identity claim another's local account by
        // asserting the same address. Only trust it when the provider explicitly marked it verified.
        string? emailVerified = principal?.FindFirstValue("email_verified");
        if (!string.Equals(emailVerified, "true", StringComparison.OrdinalIgnoreCase))
        {
            context.HandleResponse();
            context.Response.Redirect("/Login?error=oidc_email_unverified");
            return;
        }

        NpgsqlDataSource db = context.HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>();
        PasswordService passwords = context.HttpContext.RequestServices.GetRequiredService<PasswordService>();
        AuditLog audit = context.HttpContext.RequestServices.GetRequiredService<AuditLog>();

        await using NpgsqlConnection conn = await db.OpenConnectionAsync(context.HttpContext.RequestAborted);

        (Guid UserId, string PasswordHash, string Role) userRow =
            await conn.GetUserByUsernameAsync(email, context.HttpContext.RequestAborted).FirstOrDefaultAsync();

        Guid userId;
        if (userRow == default)
        {
            // First SSO login for this identity — auto-provision as the least-privileged
            // role. There's no admin UI to pre-create accounts, and unknown-OIDC-identity ==
            // reject would make SSO unusable; a random, never-communicated password hash makes
            // password-based login for this account practically impossible.
            string randomPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            UsernameResult created = await conn
                .InsertViewerUserAsync(email, passwords.Hash(randomPassword), context.HttpContext.RequestAborted)
                .FirstAsync();

            (Guid UserId, string PasswordHash, string Role) createdRow = await conn
                .GetUserByUsernameAsync(created.Username, context.HttpContext.RequestAborted)
                .FirstAsync();
            userId = createdRow.UserId;

            await audit.WriteAsync("system", "oidc.user_provisioned", userId.ToString(), ct: context.HttpContext.RequestAborted);
        }
        else
        {
            userId = userRow.UserId;
        }

        string sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddHours(24);

        await conn.InsertUserSessionAsync(
                sessionId,
                userId,
                expiresAt,
                context.HttpContext.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
                context.HttpContext.Connection.RemoteIpAddress,
                context.HttpContext.RequestAborted
            )
            .ExecuteAsync(context.HttpContext.RequestAborted);

        context.HttpContext.Response.Cookies.Append(
            SessionMiddleware.CookieName,
            sessionId,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Expires = expiresAt,
            }
        );

        await audit.WriteAsync($"user:{email}", "login.oidc", userId.ToString(), ct: context.HttpContext.RequestAborted);

        // Never let the OIDC handler complete its own SignInAsync against SignInScheme —
        // our jmw_session cookie above is the only session that should exist.
        context.HandleResponse();
        context.Response.Redirect("/");
    }
}