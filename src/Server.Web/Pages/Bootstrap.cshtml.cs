using System.Security.Cryptography;
using System.Text;

using JMW.Discovery.Server.Audit;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

using Npgsql;

namespace JMW.Discovery.Server.Pages;

[EnableRateLimiting("bootstrap")]
public sealed class BootstrapModel : PageModel
{
    private readonly AuditLog _audit;
    private readonly PasswordService _passwords;
    private readonly NpgsqlDataSource _db;
    private readonly BootstrapSetupToken _setupToken;

    public BootstrapModel(NpgsqlDataSource db, PasswordService passwords, AuditLog audit, BootstrapSetupToken setupToken)
    {
        _db = db;
        _passwords = passwords;
        _audit = audit;
        _setupToken = setupToken;
    }

    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    [BindProperty]
    public string SetupToken { get; set; } = string.Empty;

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await AdminExistsAsync(ct))
        {
            return Redirect("/");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (await AdminExistsAsync(ct))
        {
            return Redirect("/");
        }

        if (!ConstantTimeEquals(SetupToken, _setupToken.Value))
        {
            ErrorMessage = "Invalid setup token. Check the server console/logs for the token printed at startup.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = "Username is required.";
            return Page();
        }

        if (Password != ConfirmPassword)
        {
            ErrorMessage = "Passwords do not match.";
            return Page();
        }

        if (Password.Length < 8)
        {
            ErrorMessage = "Password must be at least 8 characters.";
            return Page();
        }

        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        await conn.InsertUserAsync(Username, _passwords.Hash(Password), ct).ExecuteAsync(ct);

        await _audit.WriteAsync("system", "bootstrap.admin_created", Username, ct: ct);

        return Redirect("/Login");
    }

    private async Task<bool> AdminExistsAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);
        AdminCountResult count = await conn.CountAdminsAsync(ct).FirstOrDefaultAsync(ct);
        return count.Count > 0;
    }

    private static bool ConstantTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}