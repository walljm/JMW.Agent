using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace JMW.Discovery.Server.Agents;

/// <summary>
/// One published agent binary, indexed from disk.
/// </summary>
public sealed record ReleaseEntry(
    string Version,
    string Os,
    string Arch,
    string Path,
    long Size,
    string Sha256,
    string Signature, // base64 ECDSA P-256 signature, "" if no .sig sidecar was found
    string Filename
);

/// <summary>
/// Scans a directory of agent binaries and serves the latest version per (os, arch)
/// target to the heartbeat handler and the download endpoint. Ported from the
/// pre-C# (Go) revision's internal/server/releases package.
///
/// Layout on disk (created by an operator, or the UpdateSign tool's output copied in):
///
///   releases/
///     v1.3.0/
///       jmw-agent-linux-x64
///       jmw-agent-linux-x64.sig
///       jmw-agent-linux-arm64
///       jmw-agent-linux-arm64.sig
///       jmw-agent-macos-x64
///       jmw-agent-macos-x64.sig
///       jmw-agent-windows-x64.exe
///       jmw-agent-windows-x64.exe.sig
///     v1.4.0/
///       ...
///
/// Filenames must match `jmw-agent-&lt;os&gt;-&lt;arch&gt;[.exe]`, where os/arch use the same
/// strings the agent reports at registration (RuntimeInformation-derived: linux, macos,
/// windows / x64, arm64, ...). The directory name must be a clean semver tag
/// (vX.Y.Z, no prerelease/build metadata) — this is the auto-update gate, so an
/// operator who drops a dev/dirty build into the releases dir by mistake cannot
/// flap the fleet onto it. The highest semver directory wins per platform.
/// A binary with no ".sig" sidecar is indexed (visible via <see cref="All" />) but
/// never offered as an update — the heartbeat endpoint only advertises entries with
/// a non-empty <see cref="ReleaseEntry.Signature" />, since the agent's Updater
/// rejects an unsigned offer anyway.
/// </summary>
public sealed class ReleaseManager
{
    private static readonly Regex BinaryNamePattern =
        new(@"^jmw-agent-([a-z0-9]+)-([a-z0-9]+)(\.exe)?$", RegexOptions.Compiled);

    // Only a clean release tag (no -dirty, -gSHA, etc) is eligible as an auto-update
    // source.
    private static readonly Regex CleanSemverPattern = new(@"^v\d+\.\d+\.\d+$", RegexOptions.Compiled);

    private readonly string? _dir;
    private readonly Lock _lock = new();
    private Dictionary<string, ReleaseEntry> _byPlatform = new(StringComparer.Ordinal);
    private Dictionary<string, ReleaseEntry> _byFilename = new(StringComparer.Ordinal);

    public ReleaseManager(string? releasesDir)
    {
        _dir = string.IsNullOrWhiteSpace(releasesDir) ? null : releasesDir;
    }

    /// <summary>Whether a releases directory is configured. When false, auto-update is a no-op.</summary>
    public bool Enabled => _dir is not null;

    /// <summary>Returns the newest signed binary on disk for the given platform, or null if none is published.</summary>
    public ReleaseEntry? Latest(string os, string arch)
    {
        if (!Enabled)
        {
            return null;
        }

        lock (_lock)
        {
            return _byPlatform.GetValueOrDefault(PlatformKey(os, arch));
        }
    }

    /// <summary>Returns the entry whose version/filename matches the given pair. Used by the download
    /// endpoint to refuse arbitrary path access.</summary>
    public ReleaseEntry? Lookup(string version, string filename)
    {
        if (!Enabled)
        {
            return null;
        }

        lock (_lock)
        {
            return _byFilename.GetValueOrDefault(version + "/" + filename);
        }
    }

    /// <summary>A snapshot of the current latest-per-platform table. Useful for admin visibility.</summary>
    public IReadOnlyList<ReleaseEntry> All()
    {
        lock (_lock)
        {
            return [.. _byPlatform.Values];
        }
    }

    public static Stream Open(ReleaseEntry entry) =>
        new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>
    /// Walks the directory and refreshes the indices. Safe to call repeatedly;
    /// cheap when nothing changed (SHA-256 is only recomputed when a file's size changed).
    /// </summary>
    public void Scan()
    {
        if (_dir is null)
        {
            return;
        }

        if (!Directory.Exists(_dir))
        {
            // Treat a missing dir as "no releases published yet" — not fatal.
            lock (_lock)
            {
                _byPlatform = new Dictionary<string, ReleaseEntry>(StringComparer.Ordinal);
                _byFilename = new Dictionary<string, ReleaseEntry>(StringComparer.Ordinal);
            }

            return;
        }

        // Preserve the SHA-256 cache across rescans, keyed by path+size, so an
        // unchanged file is never re-hashed.
        Dictionary<(string Path, long Size), string> priorHashes;
        lock (_lock)
        {
            priorHashes = _byPlatform.Values
                .Concat(_byFilename.Values)
                .GroupBy(e => (e.Path, e.Size))
                .ToDictionary(g => g.Key, g => g.First().Sha256);
        }

        Dictionary<string, ReleaseEntry> newByPlatform = new(StringComparer.Ordinal);
        Dictionary<string, ReleaseEntry> newByFilename = new(StringComparer.Ordinal);

        foreach (string versionDir in Directory.EnumerateDirectories(_dir))
        {
            string version = Path.GetFileName(versionDir);
            if (!CleanSemverPattern.IsMatch(version))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(versionDir))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Match match = BinaryNamePattern.Match(name);
                if (!match.Success)
                {
                    continue;
                }

                string os = match.Groups[1].Value;
                string arch = match.Groups[2].Value;

                FileInfo info = new(file);
                long size = info.Length;

                if (!priorHashes.TryGetValue((file, size), out string? sha256))
                {
                    sha256 = HashFile(file);
                }

                string signature = ReadSignature(file + ".sig");

                ReleaseEntry entry = new(
                    Version: version,
                    Os: os,
                    Arch: arch,
                    Path: file,
                    Size: size,
                    Sha256: sha256,
                    Signature: signature,
                    Filename: name
                );

                string platformKey = PlatformKey(os, arch);
                if (!newByPlatform.TryGetValue(platformKey, out ReleaseEntry? current)
                 || SemverGreater(current.Version, entry.Version))
                {
                    newByPlatform[platformKey] = entry;
                }

                newByFilename[version + "/" + name] = entry;
            }
        }

        lock (_lock)
        {
            _byPlatform = newByPlatform;
            _byFilename = newByFilename;
        }
    }

    private static string PlatformKey(string os, string arch) => os + "/" + arch;

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    private static string ReadSignature(string sigPath) =>
        File.Exists(sigPath) ? File.ReadAllText(sigPath).Trim() : "";

    private static readonly Regex SemverPattern =
        new(@"^v(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$", RegexOptions.Compiled);

    /// <summary>Reports whether <paramref name="b" /> is strictly greater than <paramref name="a" /> in semver order.
    /// Non-parseable inputs fall back to an ordinal string compare so a malformed/dev version is
    /// still handled deterministically instead of throwing.</summary>
    public static bool SemverGreater(string a, string b)
    {
        if (a == b)
        {
            return false;
        }

        Match am = SemverPattern.Match(a);
        Match bm = SemverPattern.Match(b);
        if (!am.Success || !bm.Success)
        {
            return string.CompareOrdinal(a, b) < 0;
        }

        for (int i = 1; i <= 3; i++)
        {
            int ai = int.Parse(am.Groups[i].Value);
            int bi = int.Parse(bm.Groups[i].Value);
            if (ai != bi)
            {
                return ai < bi;
            }
        }

        string aPre = am.Groups[4].Value;
        string bPre = bm.Groups[4].Value;

        // A release with no prerelease qualifier is greater than one with, at the
        // same MAJOR.MINOR.PATCH.
        if (aPre.Length == 0 && bPre.Length > 0)
        {
            return false; // a (no prerelease) is already greater than b — b is not greater than a
        }

        if (aPre.Length > 0 && bPre.Length == 0)
        {
            return true; // b (no prerelease) is greater than a
        }

        return string.CompareOrdinal(aPre, bPre) < 0;
    }
}