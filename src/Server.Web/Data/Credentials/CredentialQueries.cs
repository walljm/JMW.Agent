using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

public static partial class CredentialQueries
{
    // ── Admin: Credentials ──────────────────────────────────────────────────────

    /// <summary>
    /// Lists credential metadata (no encrypted_blob) with an optional exact type filter and
    /// keyset pagination. Pass null for afterCreatedAt/afterCredentialId to start from the
    /// first page.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(Guid CredentialId, string Name, string Type, DateTimeOffset CreatedAt, DateTimeOffset
            UpdatedAt)>
        ListCredentialsAsync(
            this NpgsqlConnection connection,
            string? type,
            DateTimeOffset? afterCreatedAt,
            Guid? afterCredentialId,
            int limit,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Returns credential metadata (no encrypted_blob) for one credential.
    /// Returns no rows if the credential id is unknown.
    /// </summary>
    [DatabaseCommand]
    public static partial
        IAsyncEnumerable<(Guid CredentialId, string Name, string Type, DateTimeOffset CreatedAt, DateTimeOffset
            UpdatedAt)>
        GetCredentialAsync(
            this NpgsqlConnection connection,
            Guid credentialId,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Returns the type and encrypted_blob for one credential, for decryption by
    /// the config assembler. Returns no rows if the credential id is unknown.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(string Type, byte[] EncryptedBlob)> GetCredentialSecretAsync(
        this NpgsqlConnection connection,
        Guid credentialId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Inserts a credential with its encrypted secret blob.
    /// Returns the inserted (credential_id, name, type, created_at).
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(Guid CredentialId, string Name, string Type, DateTimeOffset CreatedAt)>
        InsertCredentialAsync(
            this NpgsqlConnection connection,
            string name,
            string type,
            byte[] encryptedBlob,
            CancellationToken cancellationToken
        );

    /// <summary>
    /// Updates a credential's name and type (not the secret).
    /// Returns the credential_id, or no rows if not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<CredentialIdResult> UpdateCredentialMetaAsync(
        this NpgsqlConnection connection,
        Guid credentialId,
        string name,
        string type,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Rotates a credential's encrypted secret blob.
    /// Returns the credential_id, or no rows if not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<CredentialIdResult> UpdateCredentialSecretAsync(
        this NpgsqlConnection connection,
        Guid credentialId,
        byte[] encryptedBlob,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes a credential only if it is not referenced by any collection or service
    /// target. Returns the credential_id when deleted; no rows when in use or not found.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<CredentialIdResult> DeleteCredentialAsync(
        this NpgsqlConnection connection,
        Guid credentialId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns whether a credential is referenced by any collection or service target.
    /// </summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<InUseResult> IsCredentialInUseAsync(
        this NpgsqlConnection connection,
        Guid credentialId,
        CancellationToken cancellationToken
    );
}