using System.Security.Cryptography.X509Certificates;

namespace JMW.Discovery.Server.Agents;

/// <summary>
/// Loads the fleet's additional trusted CA certificates from server configuration and
/// exposes them as PEM strings for delivery to agents in the heartbeat config
/// (<see cref="JMW.Discovery.Core.HeartbeatConfig.TrustedCaCertificates" />). Every agent
/// receives the same set — this is fleet-wide trust policy, not per-agent config.
/// Source: the JMW_TRUSTED_CA_PATH environment variable, pointing at either a single PEM
/// file or a directory of *.pem/*.crt/*.cer files (mirroring the JMW_KEY_RING_PATH
/// convention). Read once at startup; adding or rotating a CA requires a server restart.
/// Unset means no extra CAs are distributed and agents fall back to system trust only.
/// These are public certificates, not secrets — safe to keep in plain configuration. The
/// natural upgrade path is a DB-managed table with an admin UI once per-zone scoping or
/// self-service management is needed.
/// </summary>
public sealed class TrustedCaProvider
{
    private static readonly string[] CertExtensions = [".pem", ".crt", ".cer"];

    /// <summary>PEM-encoded CA certificates to distribute. Empty when none are configured.</summary>
    public IReadOnlyList<string> Certificates { get; }

    public TrustedCaProvider(ILogger<TrustedCaProvider> logger)
    {
        Certificates = Load(logger);
    }

    private static List<string> Load(ILogger logger)
    {
        string? path = Environment.GetEnvironmentVariable("JMW_TRUSTED_CA_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        List<string> files = new();
        if (File.Exists(path))
        {
            files.Add(path);
        }
        else if (Directory.Exists(path))
        {
            files.AddRange(
                Directory.EnumerateFiles(path)
                    .Where(f => CertExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.Ordinal)
            );
        }
        else
        {
            TrustedCaProviderLog.PathNotFound(logger, path);
            return [];
        }

        List<string> pems = new(files.Count);
        foreach (string file in files)
        {
            string pem;
            try
            {
                pem = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                TrustedCaProviderLog.ReadFailed(logger, file, ex);
                continue;
            }

            // Validate it parses as at least one certificate before distributing it, so a
            // misconfigured file surfaces in the server log instead of silently at the agent.
            try
            {
                X509Certificate2Collection parsed = new();
                parsed.ImportFromPem(pem);
                if (parsed.Count == 0)
                {
                    TrustedCaProviderLog.NoCertificates(logger, file);
                    continue;
                }
            }
            catch (Exception ex)
            {
                TrustedCaProviderLog.InvalidCertificate(logger, file, ex);
                continue;
            }

            pems.Add(pem);
        }

        TrustedCaProviderLog.Loaded(logger, pems.Count);
        return pems;
    }
}

internal static partial class TrustedCaProviderLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "JMW_TRUSTED_CA_PATH '{Path}' does not exist; no extra CAs distributed."
    )]
    internal static partial void PathNotFound(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read trusted CA file '{File}'.")]
    internal static partial void ReadFailed(ILogger logger, string file, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trusted CA file '{File}' contains no certificates.")]
    internal static partial void NoCertificates(ILogger logger, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trusted CA file '{File}' is not valid PEM.")]
    internal static partial void InvalidCertificate(ILogger logger, string file, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Loaded {Count} trusted CA certificate file(s) for agent distribution."
    )]
    internal static partial void Loaded(ILogger logger, int count);
}