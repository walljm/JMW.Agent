using System.Reflection;

using Microsoft.Extensions.FileProviders;

namespace ITPIE.Migrations;

internal sealed class DatabaseMigrationScriptProvider
{
    private readonly IFileProvider _fileProvider;

    public DatabaseMigrationScriptProvider(Assembly scriptsAssembly)
    {
        _fileProvider = new ManifestEmbeddedFileProvider(scriptsAssembly, "Scripts");
    }

    /// <summary>
    /// Enumerates the files and directories at the given path, recursively.
    /// </summary>
    /// <returns>The contents of all directories, recursively.</returns>
    private static IEnumerable<IFileInfo> GetDirectoryContentsRecursive(IFileProvider fileProvider, string subpath)
    {
        foreach (IFileInfo fileInfo in fileProvider.GetDirectoryContents(subpath))
        {
            if (fileInfo.IsDirectory)
            {
                string innerSubpath = Path.Combine(subpath, fileInfo.Name);
                foreach (IFileInfo innerFileInfo in GetDirectoryContentsRecursive(fileProvider, innerSubpath))
                {
                    yield return innerFileInfo;
                }
            }
            else
            {
                yield return fileInfo;
            }
        }
    }

    /// <summary>
    /// Gets the ordered migrations from the Ordered folder.
    /// Migrations are run once and tracked in the schemaversions table.
    /// </summary>
    public IEnumerable<DatabaseMigrationScript> GetOrderedMigrations() =>
        GetDirectoryContentsRecursive(_fileProvider, "Ordered")
            .Select(f => new DatabaseMigrationScript(f))
            .OrderBy(m => m.ScriptName);
}