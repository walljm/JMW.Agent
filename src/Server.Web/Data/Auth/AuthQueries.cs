using System.Net;

using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

public static partial class AuthQueries
{
    // ── Session ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates last_seen and returns (user_id, username, role) for a valid, non-expired session.
    /// Returns no rows if the session does not exist or is expired.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid UserId, string Username, string Role)> LoadSessionAsync(
        this NpgsqlConnection connection,
        string sessionId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes a session by session_id. Returns the deleted session_id.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<SessionIdResult> DeleteSessionAsync(
        this NpgsqlConnection connection,
        string sessionId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes all other sessions for a user (all except the caller's current session).
    /// Returns the deleted session IDs. Called on password change to revoke concurrent sessions.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<SessionIdResult> DeleteOtherSessionsAsync(
        this NpgsqlConnection connection,
        Guid userId,
        string currentSessionId,
        CancellationToken cancellationToken
    );

    // ── Bootstrap ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns COUNT(*) of users with role = 'admin'.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<AdminCountResult> CountAdminsAsync(
        this NpgsqlConnection connection,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Inserts a new admin user. Returns the inserted username.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<UsernameResult> InsertUserAsync(
        this NpgsqlConnection connection,
        string username,
        string passwordHash,
        CancellationToken cancellationToken
    );

    // ── Auth ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns (user_id, password_hash, role) for a user by username.
    /// Returns no rows if the user is not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid UserId, string PasswordHash, string Role)> GetUserByUsernameAsync(
        this NpgsqlConnection connection,
        string username,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Updates a user's password hash. Returns the user_id, or no rows if the user is not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<UserIdResult> UpdateUserPasswordAsync(
        this NpgsqlConnection connection,
        Guid userId,
        string passwordHash,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Inserts a new user with the least-privileged 'viewer' role — used to auto-provision an
    /// account on a user's first successful OIDC login. Returns the inserted username.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<UsernameResult> InsertViewerUserAsync(
        this NpgsqlConnection connection,
        string username,
        string passwordHash,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Inserts a new user session. Returns the inserted session_id.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<SessionIdResult> InsertUserSessionAsync(
        this NpgsqlConnection connection,
        string sessionId,
        Guid userId,
        DateTimeOffset expiresAt,
        string? userAgent,
        IPAddress? ipAddress,
        CancellationToken cancellationToken
    );

    // ── Admin: Users ─────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all users with their most recent session activity, oldest account first.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(Guid UserId, string Username, string Role, DateTimeOffset CreatedAt, DateTimeOffset?
            LastSeen)> ListUsersAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Sets a user's role ('admin' or 'viewer'). Returns the user_id, or no rows if not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<UserIdResult> UpdateUserRoleAsync(
        this NpgsqlConnection connection,
        Guid userId,
        string role,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes a user (cascades to their sessions). Returns the user_id, or no rows if not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<UserIdResult> DeleteUserAsync(
        this NpgsqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken
    );
}