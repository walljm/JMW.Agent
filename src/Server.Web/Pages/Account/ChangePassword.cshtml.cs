using System.Security.Claims;

using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Npgsql;

namespace JMW.Discovery.Server.Pages.Account;

[Authorize(Policy = RbacPolicies.Authenticated)]
public sealed class ChangePasswordModel : PageModel
{
    private readonly AuditLog _audit;
    private readonly PasswordService _passwords;
    private readonly NpgsqlDataSource _db;

    public ChangePasswordModel(NpgsqlDataSource db, PasswordService passwords, AuditLog audit)
    {
        _db = db;
        _passwords = passwords;
        _audit = audit;
    }

    [BindProperty]
    public string CurrentPassword { get; set; } = string.Empty;

    [BindProperty]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        string? username = User.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "New passwords do not match.";
            return Page();
        }

        if (NewPassword.Length < 8)
        {
            ErrorMessage = "New password must be at least 8 characters.";
            return Page();
        }

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        (Guid UserId, string PasswordHash, string Role) userRow =
            await conn.GetUserByUsernameAsync(username, ct).FirstOrDefaultAsync(ct);

        if (userRow == default || !_passwords.Verify(CurrentPassword, userRow.PasswordHash))
        {
            await _audit.WriteAsync($"user:{username}", "password_change.failure", null, ct: ct);
            ErrorMessage = "Current password is incorrect.";
            return Page();
        }

        await conn.UpdateUserPasswordAsync(userRow.UserId, _passwords.Hash(NewPassword), ct).ExecuteAsync(ct);

        // Revoke all other sessions for this user so a stolen cookie can't outlast
        // the password change. Keep only the current session (the one performing the change).
        string? currentSessionId = User.FindFirstValue("session_id");
        if (!string.IsNullOrEmpty(currentSessionId))
        {
            await conn.DeleteOtherSessionsAsync(userRow.UserId, currentSessionId, ct).ExecuteAsync(ct);
        }

        await _audit.WriteAsync($"user:{username}", "password_change.success", userRow.UserId.ToString(), ct: ct);

        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;
        SuccessMessage = "Password changed.";
        return Page();
    }
}