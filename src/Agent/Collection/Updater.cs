using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;

using JMW.Discovery.Core;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Downloads, verifies, and installs a binary self-update offered by the server.
/// Security chain:
/// 1. URL host must match the configured server host (no external redirects).
/// 2. Binary is downloaded to a temp file in the same directory as the running
/// executable (same-filesystem guarantees os.Rename is atomic).
/// 3. SHA-256 of the downloaded bytes is compared to the server-advertised hash.
/// 4. ECDSA P-256 signature is verified against the canonical metadata string
/// using the public key baked into UpdatePublicKey.Value.
/// 5. On pass: atomic rename replaces the running binary.
/// 6. Process.Start (new binary) + Environment.Exit(0) — service manager restarts.
/// On Windows the running .exe is locked by the OS. Instead of rename, the new
/// binary is written as {exe}.new and the process exits; the installer/service
/// wrapper promotes it on next launch.
/// </summary>
public static class Updater
{
    /// <summary>
    /// The signature algorithm identifier. The server must send this exact string
    /// or the update is rejected, making the algorithm negotiated not assumed.
    /// </summary>
    public const string Algorithm = AgentUpdateSigning.Algorithm;

    private static readonly ILogger Log = AgentLog.Factory.CreateLogger(nameof(Updater));
    private static readonly SemaphoreSlim _inFlight = new(1, 1);

    /// <param name="info">Update offer from the server heartbeat.</param>
    /// <param name="serverUri">Base URI of the server — update URL must share the same host.</param>
    /// <param name="apiKey">
    /// The agent's API key. The download endpoint sits behind the same
    /// AgentApiKeyMiddleware as every other /api/v1/agent/* route, so the request
    /// needs the same Bearer auth as the heartbeat/facts calls.
    /// </param>
    /// <param name="http">Shared HttpClient (already configured with base address).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ApplyAsync(
        UpdateInfo info,
        Uri serverUri,
        string apiKey,
        HttpClient http,
        CancellationToken ct
    )
    {
        ValidateOffer(info, serverUri);

        if (!_inFlight.Wait(0, CancellationToken.None))
        {
            throw new InvalidOperationException("Another update is already in progress.");
        }

        try
        {
            await ApplyCoreAsync(info, apiKey, http, ct);
        }
        finally
        {
            _inFlight.Release();
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private static void ValidateOffer(UpdateInfo info, Uri serverUri)
    {
        if (string.IsNullOrWhiteSpace(info.Url))
        {
            throw new ArgumentException("Update URL is empty.");
        }

        if (string.IsNullOrWhiteSpace(info.Sha256) || info.Sha256.Length != 64)
        {
            throw new ArgumentException("Update SHA-256 must be a 64-character hex string.");
        }

        if (info.SignatureAlgorithm != Algorithm)
        {
            throw new ArgumentException(
                $"Unsupported signature algorithm '{info.SignatureAlgorithm}'. Expected '{Algorithm}'."
            );
        }

        if (string.IsNullOrWhiteSpace(info.Signature))
        {
            throw new ArgumentException("Update signature is empty.");
        }

        if (string.IsNullOrWhiteSpace(UpdatePublicKey.Value))
        {
            throw new InvalidOperationException(
                "UpdatePublicKey.Value is not set. Rebuild with a real key before deploying."
            );
        }

        // Prevent a compromised server from redirecting downloads to an external host.
        Uri updateUri = new(info.Url);
        if (!updateUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Update URL must use HTTPS.");
        }

        if (!updateUri.Host.Equals(serverUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Update URL host '{updateUri.Host}' does not match server host '{serverUri.Host}'."
            );
        }
    }

    // ── Core apply ────────────────────────────────────────────────────────────

    private static async Task ApplyCoreAsync(UpdateInfo info, string apiKey, HttpClient http, CancellationToken ct)
    {
        string exePath = ResolveExePath();
        string exeDir = Path.GetDirectoryName(exePath)
         ?? throw new InvalidOperationException("Cannot determine executable directory.");

        // Temp file in same directory as the running binary — same-fs for atomic rename.
        string tmpPath = Path.Combine(exeDir, $".update-{Guid.NewGuid():N}.tmp");

        try
        {
            (string actualHash, long actualSize) = await DownloadAsync(info.Url, tmpPath, apiKey, http, ct);
            VerifyHash(info, actualHash, actualSize);
            VerifySignature(info, actualHash, actualSize);

            Apply(exePath, tmpPath);
        }
        catch
        {
            // Always remove partial/failed download — don't leave cruft.
            if (File.Exists(tmpPath))
            {
                File.Delete(tmpPath);
            }

            throw;
        }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    private static async Task<(string Hash, long Size)> DownloadAsync(
        string url,
        string tmpPath,
        string apiKey,
        HttpClient http,
        CancellationToken ct
    )
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using HttpResponseMessage response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        response.EnsureSuccessStatusCode();

        using SHA256 sha = SHA256.Create();
        await using Stream body = await response.Content.ReadAsStreamAsync(ct);
        await using FileStream tmp = File.OpenWrite(tmpPath);
        await using CryptoStream tee = new(tmp, sha, CryptoStreamMode.Write, leaveOpen: true);

        long written = 0;
        byte[] buf = new byte[81_920]; // 80 KB — one I/O op per typical MTU burst
        int read;
        while ((read = await body.ReadAsync(buf, ct)) > 0)
        {
            await tee.WriteAsync(buf.AsMemory(0, read), ct);
            written += read;
        }

        // Flush the CryptoStream to finalize the hash.
        await tee.FlushFinalBlockAsync(ct);

        string hash = Convert
            .ToHexString(sha.Hash ?? throw new InvalidOperationException("SHA256 hash was not computed."))
            .ToLowerInvariant();
        return (hash, written);
    }

    // ── Verification ──────────────────────────────────────────────────────────

    private static void VerifyHash(UpdateInfo info, string actualHash, long actualSize)
    {
        if (!actualHash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SHA-256 mismatch: got {actualHash}, expected {info.Sha256}."
            );
        }

        if (info.Size > 0 && actualSize != info.Size)
        {
            throw new InvalidDataException(
                $"Size mismatch: got {actualSize}, expected {info.Size}."
            );
        }
    }

    private static void VerifySignature(UpdateInfo info, string actualHash, long actualSize)
    {
        // The signed payload is a canonical metadata string, not the binary itself
        // (see AgentUpdateSigning). This binds the signature to a specific
        // version+hash, preventing reuse: a valid signature for v1.0.0 cannot be
        // applied to v1.0.1 even if the SHA-256 happened to match.
        string filename = Path.GetFileName(new Uri(info.Url).AbsolutePath);

        byte[] sigBytes;
        byte[] keyBytes;
        try
        {
            sigBytes = Convert.FromBase64String(info.Signature);
            keyBytes = Convert.FromBase64String(UpdatePublicKey.Value);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Signature or public key is not valid base64.", ex);
        }

        using ECDsa ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(keyBytes, out _);

        if (!AgentUpdateSigning.Verify(ecdsa, info.Version, filename, actualHash, actualSize, sigBytes))
        {
            throw new InvalidDataException("Update signature verification failed.");
        }
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    private static void Apply(string exePath, string tmpPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows locks the running .exe. Stage the binary and exit cleanly;
            // the service wrapper or installer promotes {exe}.new on next launch.
            string staged = exePath + ".new";
            File.Move(tmpPath, staged, overwrite: true);

            UpdaterLog.UpdateStaged(Log, staged);
            Environment.Exit(0);
        }
        else
        {
            // Unix: rename is atomic if src and dst are on the same filesystem.
            // The kernel keeps the old inode open; running code is unaffected.
            File.SetUnixFileMode(
                tmpPath,
                UnixFileMode.UserRead
              | UnixFileMode.UserWrite
              | UnixFileMode.UserExecute
              | UnixFileMode.GroupRead
              | UnixFileMode.GroupExecute
              | UnixFileMode.OtherRead
              | UnixFileMode.OtherExecute
            );

            File.Move(tmpPath, exePath, overwrite: true);

            UpdaterLog.BinaryReplaced(Log);

            // Start new binary before exiting so there is no window where neither
            // binary is running. The service manager may also restart on exit.
            string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            Process.Start(
                new ProcessStartInfo(exePath, args)
                {
                    UseShellExecute = false,
                }
            );
            Environment.Exit(0);
        }
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static string ResolveExePath()
    {
        string path = Environment.ProcessPath
         ?? throw new InvalidOperationException("Cannot determine executable path.");

        // Resolve symlinks so we replace the real file, not a link target.
        FileInfo info = new(path);
        FileSystemInfo? real = info.ResolveLinkTarget(returnFinalTarget: true);
        return real?.FullName ?? path;
    }
}

internal static partial class UpdaterLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Update staged at '{StagedPath}'. Restarting to apply.")]
    public static partial void UpdateStaged(ILogger logger, string stagedPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Binary replaced. Restarting.")]
    public static partial void BinaryReplaced(ILogger logger);
}