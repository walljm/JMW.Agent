using System.Text.Json;
using System.Text.Json.Serialization;

using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

using Microsoft.Extensions.Logging;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Reads the step-ca client configuration (defaults.json) and emits facts
/// about the CA that this device trusts / is enrolled with.
/// This is the CLIENT perspective — contrast with StepCaCollector which
/// collects facts about a CA SERVER running on this host.
/// Preconditions:
/// 1. The `step` CLI binary must be present on PATH.
/// 2. A defaults.json must exist in $STEPPATH/config/ or ~/.step/config/.
/// 3. defaults.json must contain a non-empty `fingerprint` field.
/// Fact keys (key dimension = root CA fingerprint):
/// Device[{deviceId}].TrustedCA[{fingerprint}].CaUrl
/// Device[{deviceId}].TrustedCA[{fingerprint}].RootPath
/// </summary>
public sealed class StepClientCollector : ILocalCollector
{
    public string Name => "step-client";
    private static readonly ILogger<StepClientCollector> Log = AgentLog.CreateLogger<StepClientCollector>();

    public bool IsSupported =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    private static readonly string[] DefaultsCandidates =
    [
        // $STEPPATH/config/defaults.json (if STEPPATH env var is set)
        ..EnvPathCandidates(),
        // ~/.step/config/defaults.json
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".step",
            "config",
            "defaults.json"
        ),
    ];

    // Static array initializer helper — evaluated once at class load.
    private static string[] EnvPathCandidates()
    {
        string? stepPath = Environment.GetEnvironmentVariable("STEPPATH");
        return stepPath is { Length: > 0 }
            ? [Path.Combine(stepPath, "config", "defaults.json")]
            : [];
    }

    public Task<IReadOnlyList<Fact>> CollectAsync(string deviceId, CancellationToken ct)
    {
        // Fast exit: step CLI must be present.
        if (!CollectorHelper.BinaryExists("step"))
        {
            return Task.FromResult<IReadOnlyList<Fact>>([]);
        }

        string? defaultsPath = DefaultsCandidates.FirstOrDefault(File.Exists);
        if (defaultsPath is null)
        {
            return Task.FromResult<IReadOnlyList<Fact>>([]);
        }

        StepDefaults? defaults;
        try
        {
            defaults = JsonSerializer.Deserialize<StepDefaults>(
                File.ReadAllText(defaultsPath),
                JsonOpts
            );
        }
        catch (Exception ex)
        {
            StepClientCollectorLog.ParseDefaultsFailed(Log, ex, defaultsPath);
            return Task.FromResult<IReadOnlyList<Fact>>([]);
        }

        if (defaults?.Fingerprint is not { Length: > 0 } fingerprint)
        {
            return Task.FromResult<IReadOnlyList<Fact>>([]);
        }

        List<Fact> facts = new();

        facts.AddIfPresent(FactPaths.TrustedCaCaUrl, [deviceId, fingerprint], defaults.CaUrl);
        facts.AddIfPresent(FactPaths.TrustedCaRootPath, [deviceId, fingerprint], defaults.Root);

        return Task.FromResult<IReadOnlyList<Fact>>(facts);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class StepDefaults
    {
        [JsonPropertyName("ca-url")]
        public string? CaUrl { get; set; }

        public string? Fingerprint { get; set; }
        public string? Root { get; set; }
    }
}

internal static partial class StepClientCollectorLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to parse step defaults.json at {Path}.")]
    public static partial void ParseDefaultsFailed(ILogger logger, Exception ex, string path);
}