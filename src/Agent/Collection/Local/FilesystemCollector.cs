using JMW.Discovery.Core;
using JMW.Discovery.Core.Analysis;

namespace JMW.Discovery.Agent.Collection.Local;

/// <summary>
/// Collects filesystem usage facts using System.IO.DriveInfo — cross-platform,
/// no subprocess needed. Reports all ready drives that have a known filesystem.
/// </summary>
public sealed class FilesystemCollector : ILocalCollector
{
    public string Name => "filesystem";
    public bool IsSupported => true;

    public Task<IReadOnlyList<Fact>> CollectAsync(string deviceId, CancellationToken ct)
    {
        List<Fact> facts = new();

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.CDRom)
            {
                continue;
            }

            if (!drive.IsReady)
            {
                continue;
            }

            // Use the mountpoint as the key dimension — stable and human-readable.
            string mp = drive.RootDirectory.FullName.TrimEnd('/').TrimEnd('\\');
            if (mp.Length == 0)
            {
                mp = "/";
            }

            string[] keys = [deviceId, mp];

            facts.Add(Fact.Create(FactPaths.FsType, keys, drive.DriveFormat));
            facts.Add(Fact.Create(FactPaths.FsTotalBytes, keys, drive.TotalSize));
            facts.Add(Fact.Create(FactPaths.FsFreeBytes, keys, drive.AvailableFreeSpace));
            facts.Add(Fact.Create(FactPaths.FsUsedBytes, keys, drive.TotalSize - drive.AvailableFreeSpace));
        }

        return Task.FromResult<IReadOnlyList<Fact>>(facts);
    }
}