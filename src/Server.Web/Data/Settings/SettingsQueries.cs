using ITPIE.Database.Abstractions;

using Npgsql;

namespace JMW.Discovery.Server.Queries;

public static partial class SettingsQueries
{
    /// <summary>Returns the single agent liveness settings row.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(int OnlineMultiplier, int OfflineCeilingSecs)>
        GetAgentLivenessSettingsAsync(
            this NpgsqlConnection connection,
            CancellationToken cancellationToken
        );

    /// <summary>Updates the agent liveness settings row. Returns the new values.</summary>
    [DatabaseCommand]
    public static partial IAsyncEnumerable<(int OnlineMultiplier, int OfflineCeilingSecs)>
        UpdateAgentLivenessSettingsAsync(
            this NpgsqlConnection connection,
            int onlineMultiplier,
            int offlineCeilingSecs,
            CancellationToken cancellationToken
        );
}