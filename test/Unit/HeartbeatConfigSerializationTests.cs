using System.Text.Json;

using JMW.Discovery.Agent;
using JMW.Discovery.Core;

namespace JMW.Discovery.UnitTests;

/// <summary>
/// The server delivers the per-agent config (intervals, collectors, targets) inside the
/// heartbeat response. The agent deserializes it with snake_case naming and no other
/// options. These tests pin that the target half of the contract survives the round-trip
/// — a regression here silently stops server-delivered collection.
/// </summary>
public sealed class HeartbeatConfigSerializationTests
{
    // Exactly the agent's HttpAgentServerClient.JsonOpts.
    private static readonly JsonSerializerOptions AgentOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void Deserialize_ServerHeartbeat_PopulatesTargets()
    {
        // The literal shape the server emits: { status, config }.
        const string json = """
            {
              "status": "approved",
              "config": {
                "heartbeat_interval_secs": 30,
                "discovery_interval_secs": 300,
                "inventory_interval_secs": 86400,
                "collectors": {},
                "targets": [
                  {
                    "collector_type": "technitium-dns",
                    "endpoint": "https://127.0.0.1:5443",
                    "label": "core-dns",
                    "credentials": { "type": "api-token", "secret": "tok123" }
                  }
                ]
              }
            }
            """;

        HeartbeatResponse? response = JsonSerializer.Deserialize<HeartbeatResponse>(json, AgentOpts);

        Assert.NotNull(response?.Config);
        TargetConfig target = Assert.Single(response.Config.Targets);
        Assert.Equal("technitium-dns", target.CollectorType);
        Assert.Equal("https://127.0.0.1:5443", target.Endpoint);
        Assert.Equal("core-dns", target.Label);
        Assert.NotNull(target.Credentials);
        Assert.Equal("api-token", target.Credentials.Type);
        Assert.Equal("tok123", target.Credentials.Secret);
    }

    [Fact]
    public void Deserialize_ServerHeartbeat_PopulatesTrustedCaCertificates()
    {
        const string json = """
            {
              "status": "approved",
              "config": {
                "heartbeat_interval_secs": 30,
                "discovery_interval_secs": 300,
                "inventory_interval_secs": 86400,
                "collectors": {},
                "targets": [],
                "trusted_ca_certificates": [
                  "-----BEGIN CERTIFICATE-----\nMIIBfoo\n-----END CERTIFICATE-----"
                ]
              }
            }
            """;

        HeartbeatResponse? response = JsonSerializer.Deserialize<HeartbeatResponse>(json, AgentOpts);

        Assert.NotNull(response?.Config?.TrustedCaCertificates);
        string pem = Assert.Single(response.Config.TrustedCaCertificates);
        Assert.Contains("BEGIN CERTIFICATE", pem, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_ServerHeartbeat_WithoutTrustedCa_IsNull()
    {
        // Backward compatibility: an older server that omits the field must still
        // deserialize, leaving the agent on system-trust-only.
        const string json = """
            {
              "status": "approved",
              "config": {
                "heartbeat_interval_secs": 30,
                "discovery_interval_secs": 300,
                "inventory_interval_secs": 86400,
                "collectors": {},
                "targets": []
              }
            }
            """;

        HeartbeatResponse? response = JsonSerializer.Deserialize<HeartbeatResponse>(json, AgentOpts);

        Assert.NotNull(response?.Config);
        Assert.Null(response.Config.TrustedCaCertificates);
    }
}