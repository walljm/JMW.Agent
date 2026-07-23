using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Auto-discovers and collects facts about a step-ca Certificate Authority
/// running on the local host.
/// Detection strategy (any match triggers collection):
/// 1. step binary is present (fast exit if not)
/// 2. ca.json exists in a standard step configuration directory
/// 3. step-ca process is found in the running process list
/// Service identity: the SHA-256 fingerprint of the root CA certificate.
/// This is the most stable identifier for a CA — it never changes unless the
/// root is deliberately rotated, and it's the same fingerprint that clients
/// pin when bootstrapping trust.
/// Facts emitted under Service[{fingerprint}].CA.*:
/// - Root CA: subject, validity window, fingerprint
/// - Intermediate CA: subject, validity window
/// - Provisioners: name → type (JWK, OIDC, ACME, etc.)
/// - CA address and DNS names from ca.json
/// - Running status from process detection
/// </summary>
public sealed class StepCaCollector : ILocalCollector
{
    public string Name => "step-ca";
    public bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
    private static readonly ILogger<StepCaCollector> Log = AgentLog.CreateLogger<StepCaCollector>();

    // Standard locations for step-ca's server configuration file.
    // ca.json is the CA SERVER config (has root, crt, key, authority.provisioners).
    // This is distinct from defaults.json which is the CLIENT config.
    private static readonly string[] CaJsonCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".step", "config", "ca.json"),
        "/etc/step-ca/config/ca.json",
        "/home/step/.step/config/ca.json",
        "/var/lib/step-ca/config/ca.json",
    ];

    // deviceId is the actual resolved DeviceId (Agent.CollectLocalAsync special-cases this
    // collector), not the "_local_" placeholder every other local collector gets — this
    // collector links a Service fact's DeviceId VALUE to the host, and a plain fact value is
    // never rewritten server-side the way a Device[...]-rooted fact's key is. Empty until the
    // agent's first cycle resolves it.
    public async Task<IReadOnlyList<Fact>> CollectAsync(string deviceId, CancellationToken ct)
    {
        // Fast exit: step CLI must be present for any collection to work.
        if (!CollectorHelper.BinaryExists("step"))
        {
            return [];
        }

        // Find the CA server config.
        string? caJsonPath = CaJsonCandidates
            .Concat(EnvPathCandidates())
            .FirstOrDefault(File.Exists);

        // If no ca.json found, check for a running step-ca process.
        bool caRunning = IsStepCaRunning();

        if (caJsonPath is null && !caRunning)
        {
            return []; // step is installed but this host is not running step-ca
        }

        List<Fact> facts = new();
        CaJsonConfig? caJson = caJsonPath is not null ? ParseCaJson(caJsonPath) : null;

        // Locate root cert: prefer ca.json path, fall back to step defaults or standard path.
        string? rootCertPath = ResolveRootCertPath(caJson);
        if (rootCertPath is null || !File.Exists(rootCertPath))
        {
            return []; // can't identify the CA without its root cert
        }

        // The root cert fingerprint is the stable service identity.
        string? fingerprint = await GetFingerprintAsync(rootCertPath, ct);
        if (fingerprint is null)
        {
            return [];
        }

        string serviceId = fingerprint; // 64-char SHA-256 hex — globally unique

        // Identity linkbacks
        facts.Add(Fact.Create(ServicePaths.Type, [serviceId], "step-ca"));
        facts.AddIfPresent(ServicePaths.DeviceId, [serviceId], deviceId);

        // ── CA status ─────────────────────────────────────────────────────────
        facts.Add(Fact.Create(ServicePaths.CaStatus, [serviceId], caRunning ? "running" : "stopped"));

        // ── CA configuration from ca.json ─────────────────────────────────────
        if (caJson is not null)
        {
            facts.AddIfPresent(ServicePaths.CaAddress, [serviceId], caJson.Address);

            foreach (string name in caJson.DnsNames ?? [])
            {
                facts.Add(Fact.Create(ServicePaths.CaDnsName, [serviceId, name], name));
            }

            foreach (CaProvisioner prov in caJson.Authority?.Provisioners ?? [])
            {
                if (prov.Name is { Length: > 0 } n && prov.Type is { Length: > 0 } t)
                {
                    facts.Add(Fact.Create(ServicePaths.CaProvisionerType, [serviceId, n], t));
                }
            }
        }

        // ── Root CA certificate ───────────────────────────────────────────────
        CertInspectResult? rootInfo = await InspectCertAsync(rootCertPath, ct);
        if (rootInfo is not null)
        {
            facts.Add(Fact.Create(ServicePaths.CaRootFingerprint, [serviceId], fingerprint));
            facts.Add(Fact.Create(ServicePaths.CaRootSubjectDn, [serviceId], rootInfo.SubjectDn ?? ""));
            facts.Add(
                Fact.Create(ServicePaths.CaRootNotBefore, [serviceId], rootInfo.Validity?.Start.ToString("o") ?? "")
            );
            facts.Add(
                Fact.Create(ServicePaths.CaRootNotAfter, [serviceId], rootInfo.Validity?.End.ToString("o") ?? "")
            );
        }

        // ── Intermediate CA certificate ───────────────────────────────────────
        string? intermediatePath = ResolveIntermediateCertPath(caJson);
        if (intermediatePath is not null && File.Exists(intermediatePath))
        {
            CertInspectResult? intInfo = await InspectCertAsync(intermediatePath, ct);
            if (intInfo is not null)
            {
                facts.Add(Fact.Create(ServicePaths.CaIntermediateSubjectDn, [serviceId], intInfo.SubjectDn ?? ""));
                facts.Add(
                    Fact.Create(
                        ServicePaths.CaIntermediateNotBefore,
                        [serviceId],
                        intInfo.Validity?.Start.ToString("o") ?? ""
                    )
                );
                facts.Add(
                    Fact.Create(
                        ServicePaths.CaIntermediateNotAfter,
                        [serviceId],
                        intInfo.Validity?.End.ToString("o") ?? ""
                    )
                );
            }
        }

        return facts;
    }

    // ── Step CLI wrappers ─────────────────────────────────────────────────────

    private static async Task<string?> GetFingerprintAsync(string certPath, CancellationToken ct)
    {
        string output = await CollectorHelper.RunAsync("step", $"certificate fingerprint {certPath}", ct);
        string fp = output.Trim();
        // step outputs raw 64-char hex for SHA-256 — no prefix, no colons.
        return fp.Length >= 32 ? fp.ToLowerInvariant() : null;
    }

    private static async Task<CertInspectResult?> InspectCertAsync(string certPath, CancellationToken ct)
    {
        string json = await CollectorHelper.RunAsync("step", $"certificate inspect --format json {certPath}", ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try { return JsonSerializer.Deserialize<CertInspectResult>(json, JsonOpts); }
        catch (Exception ex)
        {
            StepCaCollectorLog.ParseCertInspectFailed(Log, ex);
            return null;
        }
    }

    // ── Process detection ─────────────────────────────────────────────────────

    private static bool IsStepCaRunning() =>
        Process.GetProcessesByName("step-ca").Length > 0;

    // ── Path resolution ───────────────────────────────────────────────────────

    private static IEnumerable<string> EnvPathCandidates()
    {
        string? stepPath = Environment.GetEnvironmentVariable("STEPPATH");
        if (stepPath is { Length: > 0 })
        {
            yield return Path.Combine(stepPath, "config", "ca.json");
        }
    }

    private static string? ResolveRootCertPath(CaJsonConfig? caJson)
    {
        // Prefer path declared in ca.json (most reliable).
        if (caJson?.Root is { Length: > 0 } r && File.Exists(r))
        {
            return r;
        }

        // Fall back to defaults.json which the step client stores locally.
        string defaultsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".step",
            "config",
            "defaults.json"
        );

        if (File.Exists(defaultsPath))
        {
            try
            {
                StepDefaults? d = JsonSerializer.Deserialize<StepDefaults>(
                    File.ReadAllText(defaultsPath),
                    JsonOpts
                );
                if (d?.Root is { Length: > 0 } dr && File.Exists(dr))
                {
                    return dr;
                }
            }
            catch (Exception ex) { StepCaCollectorLog.ParseDefaultsFailed(Log, ex); }
        }

        // Well-known default location.
        string standard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".step",
            "certs",
            "root_ca.crt"
        );
        return File.Exists(standard) ? standard : null;
    }

    private static string? ResolveIntermediateCertPath(CaJsonConfig? caJson)
    {
        if (caJson?.Crt is { Length: > 0 } c && File.Exists(c))
        {
            return c;
        }

        string standard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".step",
            "certs",
            "intermediate_ca.crt"
        );
        return File.Exists(standard) ? standard : null;
    }

    // ── ca.json parsing ───────────────────────────────────────────────────────

    private static CaJsonConfig? ParseCaJson(string path)
    {
        try { return JsonSerializer.Deserialize<CaJsonConfig>(File.ReadAllText(path), JsonOpts); }
        catch (Exception ex)
        {
            StepCaCollectorLog.ParseCaJsonFailed(Log, ex, path);
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ── JSON shapes ───────────────────────────────────────────────────────────

    private sealed class CaJsonConfig
    {
        public string? Root { get; set; } // root cert path
        public string? Crt { get; set; } // intermediate cert path
        public string? Address { get; set; } // e.g. ":9000"
        public string[]? DnsNames { get; set; }
        public CaAuthority? Authority { get; set; }
    }

    private sealed class CaAuthority
    {
        public List<CaProvisioner> Provisioners { get; set; } = [];
    }

    private sealed class CaProvisioner
    {
        public string? Type { get; set; }
        public string? Name { get; set; }
    }

    private sealed class StepDefaults
    {
        [JsonPropertyName("ca-url")]
        public string? CaUrl { get; set; }

        public string? Fingerprint { get; set; }
        public string? Root { get; set; }
    }

    private sealed class CertInspectResult
    {
        [JsonPropertyName("subject_dn")]
        public string? SubjectDn { get; set; }

        public CertValidity? Validity { get; set; }
    }

    private sealed class CertValidity
    {
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset End { get; set; }
    }
}

internal static partial class StepCaCollectorLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to parse step certificate inspect output.")]
    public static partial void ParseCertInspectFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to parse step defaults.json.")]
    public static partial void ParseDefaultsFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to parse {CaJsonPath}.")]
    public static partial void ParseCaJsonFailed(ILogger logger, Exception ex, string caJsonPath);
}