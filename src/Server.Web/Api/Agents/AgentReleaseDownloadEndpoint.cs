using JMW.Discovery.Server.Api;

namespace JMW.Discovery.Server.Agents;

/// <summary>
/// Serves an agent binary offered via the heartbeat's update block. Sits under the
/// agent-facing route group, so <c>AgentApiKeyMiddleware</c> requires the same
/// Bearer API key as every other /api/v1/agent/* call — the binary isn't secret,
/// but there's no reason to make it fetchable by an unauthenticated caller either.
/// </summary>
public static class AgentReleaseDownloadEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/releases/{version}/{filename}", Download);
    }

    private static IResult Download(string version, string filename, ReleaseManager releases)
    {
        // Reject path traversal outright — Lookup below already refuses anything it
        // hasn't indexed, but this keeps a malformed segment from ever reaching disk I/O.
        if (version.Contains('/') || version.Contains('\\')
         || filename.Contains('/') || filename.Contains('\\'))
        {
            return ApiError.Problem(400, "bad_request", "Invalid release path.");
        }

        ReleaseEntry? entry = releases.Lookup(version, filename);
        if (entry is null)
        {
            return ApiError.NotFound("Release not found.");
        }

        Stream stream = ReleaseManager.Open(entry);
        return Results.Stream(stream, "application/octet-stream", entry.Filename);
    }
}