using System.Text.Json;

using JMW.Discovery.Core;
using JMW.Discovery.Server.Auth;
using JMW.Discovery.Server.Queries;

using Npgsql;

namespace JMW.Discovery.Server.Agents;

/// <summary>
/// Assembles the per-agent <see cref="HeartbeatConfig" /> delivered in the heartbeat
/// response. Reads the agent's intervals and collectors_config, its enabled targets, and
/// decrypts each target's credential for the agent to use. Never logs decrypted secrets —
/// the only channel is the HTTPS heartbeat response.
/// </summary>
public sealed class AgentConfigAssembler
{
    private readonly CredentialProtector _protector;
    private readonly NpgsqlDataSource _db;
    private readonly TrustedCaProvider _trustedCa;

    public AgentConfigAssembler(NpgsqlDataSource db, CredentialProtector protector, TrustedCaProvider trustedCa)
    {
        _db = db;
        _protector = protector;
        _trustedCa = trustedCa;
    }

    public async Task<HeartbeatConfig?> AssembleAsync(Guid agentId, CancellationToken ct)
    {
        await using NpgsqlConnection conn = await _db.OpenConnectionAsync(ct);

        List<(Guid AgentId, string Hostname, string Status, int HeartbeatIntervalSecs, int DiscoveryIntervalSecs, int
            InventoryIntervalSecs, JsonElement CollectorsConfig, DateTimeOffset? ClearTrackersRequestedAt,
            DateTimeOffset? LogsRequestedAt, int? LogsRequestedLines, string? LogsRequestedBefore)>
            configRows = await conn.GetAgentConfigAsync(agentId, ct).ToListAsync(ct);
        if (configRows.Count == 0)
        {
            return null;
        }

        (Guid AgentId, string Hostname, string Status, int HeartbeatIntervalSecs, int DiscoveryIntervalSecs, int
            InventoryIntervalSecs, JsonElement CollectorsConfig, DateTimeOffset? ClearTrackersRequestedAt,
            DateTimeOffset? LogsRequestedAt, int? LogsRequestedLines, string? LogsRequestedBefore) config =
            configRows[0];
        Dictionary<string, CollectorSetting> collectors = ParseCollectors(config.CollectorsConfig);

        // Materialize targets first — the reader must be closed before we run the per-target
        // credential lookups and MAC resolution (a connection serves one reader at a time).
        List<(Guid TargetId, string Endpoint, string CollectorType, Guid? CredentialId, string? Label, bool Enabled,
                string EndpointKind)>
            targetRows = await conn.ListTargetsForAgentAsync(agentId, ct).ToListAsync(ct);

        List<TargetConfig> targets = new(targetRows.Count);
        foreach ((Guid TargetId, string Endpoint, string CollectorType, Guid? CredentialId, string? Label, bool
            Enabled, string EndpointKind) row in targetRows)
        {
            // Resolve a mac-kind target to the device's current IP so collection follows DHCP
            // moves. The agent contract is unchanged — it always receives a concrete address. If
            // the MAC has never been seen on this LAN there's nothing to resolve yet, so skip the
            // target this cycle (it self-heals once ARP/DHCP/discovery records the device; the
            // Agent Detail page surfaces the unresolved state to the operator).
            string endpoint = row.Endpoint;
            if (row.EndpointKind == "mac")
            {
                ResolvedIpResult resolved =
                    await conn.GetIpForMacAsync(row.Endpoint, agentId, ct).FirstOrDefaultAsync(ct);
                if (resolved.Ip is not { } resolvedIp)
                {
                    continue;
                }

                endpoint = resolvedIp;
            }

            TargetCredential? credential = null;
            if (row.CredentialId is { } credId)
            {
                List<(string Type, byte[] EncryptedBlob)> secretRows =
                    await conn.GetCredentialSecretAsync(credId, ct).ToListAsync(ct);
                if (secretRows.Count > 0)
                {
                    credential = new TargetCredential(
                        Type: secretRows[0].Type,
                        Secret: _protector.Decrypt(secretRows[0].EncryptedBlob)
                    );
                }
            }

            targets.Add(new TargetConfig(endpoint, row.CollectorType, row.Label, credential));
        }

        return new HeartbeatConfig(
            HeartbeatIntervalSecs: config.HeartbeatIntervalSecs,
            DiscoveryIntervalSecs: config.DiscoveryIntervalSecs,
            InventoryIntervalSecs: config.InventoryIntervalSecs,
            Collectors: collectors,
            Targets: targets,
            TrustedCaCertificates: _trustedCa.Certificates,
            ClearTrackersRequestedAt: config.ClearTrackersRequestedAt,
            LogsRequestedAt: config.LogsRequestedAt,
            LogsRequestedLines: config.LogsRequestedLines,
            LogsRequestedBefore: config.LogsRequestedBefore
        );
    }

    private static Dictionary<string, CollectorSetting> ParseCollectors(JsonElement collectorsConfig)
    {
        Dictionary<string, CollectorSetting> result = new(StringComparer.Ordinal);
        if (collectorsConfig.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (JsonProperty prop in collectorsConfig.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            bool enabled = true;
            int? intervalSecs = null;

            if (prop.Value.TryGetProperty("enabled", out JsonElement enabledEl)
             && (enabledEl.ValueKind == JsonValueKind.True || enabledEl.ValueKind == JsonValueKind.False))
            {
                enabled = enabledEl.GetBoolean();
            }

            if (prop.Value.TryGetProperty("interval_secs", out JsonElement intervalEl)
             && intervalEl.ValueKind == JsonValueKind.Number)
            {
                intervalSecs = intervalEl.GetInt32();
            }

            result[prop.Name] = new CollectorSetting(enabled, intervalSecs);
        }

        return result;
    }
}