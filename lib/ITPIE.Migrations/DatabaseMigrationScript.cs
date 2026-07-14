using Microsoft.Extensions.FileProviders;

namespace ITPIE.Migrations;

/// <summary>
/// A migration script for a PostgreSQL database.
/// </summary>
internal sealed class DatabaseMigrationScript
{
    private readonly IFileInfo _fileInfo;

    public DatabaseMigrationScript(IFileInfo fileInfo)
    {
        _fileInfo = fileInfo;
    }

    /// <summary>
    /// The filename of the script, not including its path.
    /// </summary>
    public string ScriptName => _fileInfo.Name;

    /// <summary>
    /// Gets the command text of the migration script by reading the stream to its end.
    /// </summary>
    public async Task<string> GetCommandTextAsync(CancellationToken cancellationToken = default)
    {
        using StreamReader reader = new(_fileInfo.CreateReadStream());
        return await reader.ReadToEndAsync(cancellationToken);
    }
}