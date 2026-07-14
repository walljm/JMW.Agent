using JMW.Discovery.Core;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// The agent's view of the server — thin interface covering all outbound calls.
/// Inject a real HTTP implementation (HttpAgentServerClient) in production;
/// stub for tests.
/// </summary>
public interface IAgentServerClient
{
    Task<AgentRegistrationResponse> RegisterAsync(AgentRegistrationRequest request, CancellationToken ct);

    /// <summary>
    /// Polls the server for approval by sending a heartbeat and interpreting the response.
    /// Returns true when approved. Returns false when still pending (caller should retry).
    /// Throws on rejection or unexpected errors.
    /// </summary>
    Task<bool> CheckApprovalAsync(
        string agentId,
        string apiKey,
        HeartbeatRequest request,
        CancellationToken ct
    );

    /// <summary>
    /// Sends a heartbeat and receives back any server directives (including
    /// update offers). Call once per collection cycle before collecting.
    /// </summary>
    Task<HeartbeatResponse> HeartbeatAsync(
        string agentId,
        string apiKey,
        HeartbeatRequest request,
        CancellationToken ct
    );

    /// <summary>
    /// Posts all collected facts for one cycle to the server.
    /// Returns server-assigned device IDs for each batch element.
    /// </summary>
    Task<AgentFactsResponse> PostFactsAsync(
        string apiKey,
        AgentFactsRequest request,
        CancellationToken ct
    );
}