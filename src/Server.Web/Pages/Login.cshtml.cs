using System.Collections.Concurrent;
using System.Security.Cryptography;

using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages;

public sealed class LoginModel : PageModel
{
    private readonly AuditLog _audit;
    private readonly PasswordService _passwords;
    private readonly NpgsqlDataSource _db;
    private readonly OidcOptions _oidc;

    public LoginModel(
        NpgsqlDataSource db,
        PasswordService passwords,
        AuditLog audit,
        OidcOptions oidc
    )
    {
        _db = db;
        _passwords = passwords;
        _audit = audit;
        _oidc = oidc;
    }

    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public bool OidcEnabled => _oidc.Enabled;

    public string? ErrorMessage { get; private set; }

    public bool IsOidcError { get; private set; }

    private static readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)>
        FailedAttempts = new();

    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(15);
    private const int MaxFailedAttempts = 5;
    private const int MaxTrackedKeys = 10_000;

    public async Task<IActionResult> OnGetAsync(string? error, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        AdminCountResult adminCount = await conn.CountAdminsAsync(ct).FirstOrDefaultAsync(ct);
        if (adminCount.Count is null or 0)
        {
            return Redirect("/bootstrap");
        }

        if (error == "oidc_no_email")
        {
            ErrorMessage = "SSO login failed: your identity provider didn't return an email address.";
            IsOidcError = true;
        }
        else if (error == "oidc_email_unverified")
        {
            ErrorMessage = "SSO login failed: your identity provider's email address is not verified.";
            IsOidcError = true;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        string rateLimitIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (IsRateLimited(rateLimitIp, Username))
        {
            ErrorMessage = "Too many login attempts. Please try again later.";
            return Page();
        }

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        (Guid UserId, string PasswordHash, string Role) userRow =
            await conn.GetUserByUsernameAsync(Username, ct).FirstOrDefaultAsync(ct);

        const string DummyHash =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        bool userFound = userRow != default;
        bool valid = _passwords.Verify(Password, userFound ? userRow.PasswordHash : DummyHash) && userFound;

        if (!valid)
        {
            string remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            RecordFailure(remoteIp, Username);
            await _audit.WriteAsync($"user:{Username}", "login.failure", null, ct: ct);
            ErrorMessage = "Invalid username or password.";
            return Page();
        }

        string successIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        ClearFailures(successIp, Username);

        if (_passwords.NeedsRehash(userRow.PasswordHash))
        {
            await conn.UpdateUserPasswordAsync(userRow.UserId, _passwords.Hash(Password), ct).ExecuteAsync(ct);
        }

        string sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddHours(24);

        await conn.InsertUserSessionAsync(
                sessionId,
                userRow.UserId,
                expiresAt,
                Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
                HttpContext.Connection.RemoteIpAddress,
                ct
            )
            .ExecuteAsync(ct);

        Response.Cookies.Append(
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

        await _audit.WriteAsync($"user:{Username}", "login", userRow.UserId.ToString(), ct: ct);

        return Redirect("/");
    }

    private static bool IsRateLimited(string ip, string username)
    {
        string key = $"{ip}:{username}";
        if (!FailedAttempts.TryGetValue(key, out (int Count, DateTimeOffset WindowStart) entry))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - entry.WindowStart > RateLimitWindow)
        {
            FailedAttempts.TryRemove(key, out _);
            return false;
        }

        return entry.Count >= MaxFailedAttempts;
    }

    private static void RecordFailure(string ip, string username)
    {
        // Prevent unbounded memory growth from fabricated usernames.
        if (FailedAttempts.Count >= MaxTrackedKeys)
        {
            FailedAttempts.Clear();
        }

        string key = $"{ip}:{username}";
        FailedAttempts.AddOrUpdate(
            key,
            _ => (1, DateTimeOffset.UtcNow),
            (_, e) => DateTimeOffset.UtcNow - e.WindowStart > RateLimitWindow
                ? (1, DateTimeOffset.UtcNow)
                : (e.Count + 1, e.WindowStart)
        );
    }

    private static void ClearFailures(string ip, string username) =>
        FailedAttempts.TryRemove($"{ip}:{username}", out _);
}