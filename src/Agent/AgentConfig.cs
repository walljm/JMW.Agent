using System.Text.Json;
using System.Text.Json.Serialization;

namespace JMW.Discovery.Agent;

/// <summary>
/// Agent configuration loaded from a JSON file.
/// Covers everything the agent needs to know at startup: server, identity,
/// collection schedule, and targets.
/// </summary>
public sealed class AgentConfig
{
    /// <summary>Base URL of the monitoring server. e.g. "https://monitor.example.com"</summary>
    public required string ServerUrl { get; init; }

    /// <summary>
    /// The heartbeat response delivers decrypted device credentials (SSH/SNMP/API tokens) and
    /// every request carries this agent's bearer API key, so a plain-HTTP ServerUrl is rejected
    /// unless this is explicitly set — lab/dev use only, never for a server reachable by other
    /// hosts on the network.
    /// </summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>Human-readable name shown in the server UI. e.g. "dc-east-01"</summary>
    public required string Name { get; init; }

    /// <summary>Network zone this agent covers. e.g. "10.0.0.0/8". Optional.</summary>
    public string? Zone { get; init; }

    /// <summary>
    /// Fallback loop cadence when the server hasn't delivered per-phase intervals. The server
    /// config block can override this with separate Heartbeat / Discovery / Inventory
    /// intervals; until it does, all three phases run at this cadence. Default 5 minutes.
    /// </summary>
    [JsonConverter(typeof(TimeSpanSecondsConverter))]
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Remote targets to collect from — devices polled over SSH/SNMP/BACnet/Modbus, and
    /// services polled over HTTP APIs (DNS servers, Home Assistant, etc.). These are
    /// configured (not auto-discovered); the discovery phase finds new devices on the
    /// subnet, this list is deep-collected on the inventory cadence. Each entry is matched
    /// to a registered IDeviceCollector or IServiceCollector by its CollectorType field.
    /// </summary>
    public IReadOnlyList<Target> Targets { get; init; } = [];

    /// <summary>Maximum simultaneous device collection sessions. Default 8.</summary>
    public int MaxConcurrency { get; init; } = 8;

    public static AgentConfig LoadFrom(string path)
    {
        string json = File.ReadAllText(path);
        AgentConfig config = JsonSerializer.Deserialize<AgentConfig>(json, JsonOptions)
         ?? throw new InvalidOperationException($"Failed to parse agent config from '{path}'.");

        Uri serverUri = new(config.ServerUrl);
        if (serverUri.Scheme != Uri.UriSchemeHttps && !config.AllowInsecureHttp)
        {
            throw new InvalidOperationException(
                $"server_url '{config.ServerUrl}' is not HTTPS. The heartbeat response carries "
              + "decrypted device credentials and every request carries this agent's API key, so "
              + "plain HTTP is rejected by default. Use an https:// server_url, or set "
              + "\"allow_insecure_http\": true in the config for lab/dev use only."
            );
        }

        return config;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}

/// <summary>
/// A remote target the agent should collect from — a device (SSH/SNMP/BACnet/Modbus) or a
/// service (HTTP API). The collector is selected by matching CollectorType against a
/// registered IDeviceCollector.CollectorType or IServiceCollector.ServiceType.
/// </summary>
public sealed class Target
{
    /// <summary>Bare host/IP for device-style collectors, or a full URL for service-style ones.</summary>
    public required string Endpoint { get; init; }

    /// <summary>
    /// Collector type hint: "ssh", "snmp", "google-wifi", "technitium-dns", "home-assistant",
    /// etc. If null, each registered device collector's CanCollect() is tried in registration
    /// order (service collectors always require an explicit type).
    /// </summary>
    public string? CollectorType { get; init; }

    /// <summary>Authentication credentials. Interpreted by the collector.</summary>
    public TargetCredentials? Credentials { get; init; }

    /// <summary>Optional human label shown in logs. Defaults to Endpoint if omitted.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Collector-specific settings: SNMP community, SSH port override, etc.
    /// Collector implementations define which keys they read here.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// Base for target credentials. Concrete types carry protocol-specific fields.
/// The "type" discriminator in JSON selects the right subtype.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SshCredentials), "ssh")]
[JsonDerivedType(typeof(SnmpCredentials), "snmp")]
[JsonDerivedType(typeof(ApiTokenCredentials), "api-token")]
[JsonDerivedType(typeof(UsernamePasswordCredentials), "username-password")]
public abstract class TargetCredentials { }

public sealed class SshCredentials : TargetCredentials
{
    public required string Username { get; init; }
    public string? Password { get; init; }
    public string? KeyFile { get; init; }
}

public sealed class SnmpCredentials : TargetCredentials
{
    public string Community { get; init; } = "public";
    public string Version { get; init; } = "2c";
}

public sealed class ApiTokenCredentials : TargetCredentials
{
    public required string Token { get; init; }
}

public sealed class UsernamePasswordCredentials : TargetCredentials
{
    public required string Username { get; init; }
    public required string Password { get; init; }
}

/// <summary>Deserializes "interval_seconds": 300 as TimeSpan.FromSeconds(300).</summary>
file sealed class TimeSpanSecondsConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
        TimeSpan.FromSeconds(reader.GetDouble());

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions o) =>
        writer.WriteNumberValue(value.TotalSeconds);
}
