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

    /// <summary>
    /// Uploads one on-demand page of captured log output to the server's in-memory cache, in
    /// response to an admin log-pull request delivered via the heartbeat config. Fire-and-forget
    /// from the agent's side — the server holds the page only transiently
    /// (docs/plans/agent-log-viewer.md).
    /// </summary>
    Task PostLogsAsync(
        string apiKey,
        AgentLogUploadRequest request,
        CancellationToken ct
    );
}

/// <summary>
/// One page of captured agent log output uploaded to the server. Serializes (snake_case)
/// field-for-field to the server's own <c>AgentLogUploadRequest</c>. <c>RequestedAt</c> echoes the
/// <c>logs_requested_at</c> the heartbeat delivered so the UI can match a page to its request;
/// <c>Source</c> is "journald" or "buffer"; <c>NextBeforeToken</c> pages to older lines (null when
/// the source has nothing older).
/// </summary>
public sealed record AgentLogUploadRequest(
    Guid AgentId,
    DateTimeOffset RequestedAt,
    string Source,
    bool Truncated,
    string Text,
    string? NextBeforeToken
);