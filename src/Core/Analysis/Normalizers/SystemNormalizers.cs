namespace JMW.Discovery.Core.Analysis.Normalizers;

/// <summary>
/// Lowercases and trims any string FactValue.
/// Accepts multiple attribute_path patterns so one instance covers all
/// enum-like string facts: OS family/distro, disk type, container/battery
/// state, vendor, kind, duplex, interface type.
/// Returns null for non-string values and empty strings after trimming.
/// </summary>
public sealed class LowercaseTrimNormalizer : INormalizer
{
    public LowercaseTrimNormalizer(IReadOnlyList<string>? patterns = null)
    {
        AttributePathPatterns = patterns;
    }

    public IReadOnlyList<string> AttributePathPatterns => field ?? [];

    public FactValue? Normalize(FactValue raw)
    {
        string? str = raw.AsString();
        if (str is null)
        {
            return null;
        }

        string trimmed = str.Trim().ToLowerInvariant();
        return trimmed.Length == 0 ? null : FactValue.FromString(trimmed);
    }
}

/// <summary>
/// Lowercases and trims any string FactValue.
/// Accepts multiple attribute_path patterns so one instance covers all
/// enum-like string facts: OS family/distro, disk type, container/battery
/// state, vendor, kind, duplex, interface type.
/// Returns null for non-string values and empty strings after trimming.
/// </summary>
public sealed class TrimNormalizer : INormalizer
{
    public TrimNormalizer(IReadOnlyList<string>? patterns = null)
    {
        AttributePathPatterns = patterns;
    }

    public IReadOnlyList<string> AttributePathPatterns => field ?? [];

    public FactValue? Normalize(FactValue raw)
    {
        string? str = raw.AsString();
        if (str is null)
        {
            return null;
        }

        string trimmed = str.Trim();
        return trimmed.Length == 0 ? null : FactValue.FromString(trimmed);
    }
}
/// <summary>
/// Strips trailing DNS dots from a string FactValue.
/// "host.local." → "host.local"
/// Used as a pipeline step after LowercaseTrimNormalizer.
/// </summary>
public sealed class TrailingDotStripper : IValueTransform
{
    public FactValue? Normalize(FactValue raw)
    {
        string? str = raw.AsString();
        if (str is null)
        {
            return null;
        }

        string stripped = str.TrimEnd('.');
        return stripped.Length == 0 ? null : FactValue.FromString(stripped);
    }
}

/// <summary>
/// Rejects empty string FactValues (after prior steps have trimmed).
/// Use as the last step in a pipeline when a non-empty result is required.
/// </summary>
public sealed class RejectEmptyString : IValueTransform
{
    public FactValue? Normalize(FactValue raw)
    {
        string? str = raw.AsString();
        return string.IsNullOrEmpty(str) ? null : raw;
    }
}

/// <summary>
/// Hostname normalizer built as a NormalizerPipeline:
/// 1. LowercaseTrimNormalizer  — lowercase + trim whitespace
/// 2. TrailingDotStripper      — "host.local." → "host.local"
/// 3. RejectEmptyString        — drop if nothing remains
/// "Router-1.example.com." → "router-1.example.com"
/// "  DESKTOP-ABC123  "   → "desktop-abc123"
/// This shows how NormalizerPipeline composes transforms into a named rule.
/// The same three steps could be inlined anywhere, but naming them makes
/// the intent clear and lets us register them once.
/// </summary>
public static class HostnameNormalizer
{
    public static NormalizerPipeline Create() => new(
        patterns: [FactPaths.SystemHostname],
        steps:
        [
            new LowercaseTrimNormalizer(),
            new TrailingDotStripper(),
            new RejectEmptyString(),
        ]
    );
}

/// <summary>
/// Normalizes S.M.A.R.T. overall health strings to one of three canonical
/// values: "PASSED", "FAILED", or "UNKNOWN".
/// </summary>
public sealed class SmartHealthNormalizer : INormalizer
{
    public IReadOnlyList<string> AttributePathPatterns => [FactPaths.DiskSmartOverallHealth];

    public FactValue? Normalize(FactValue raw)
    {
        string? str = raw.AsString();
        if (str is null)
        {
            return null;
        }

        string lower = str.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(lower))
        {
            return null;
        }

        if (lower is "passed" or "pass" or "ok" or "good" or "healthy")
        {
            return FactValue.FromString("PASSED");
        }

        if (lower is "failed" or "fail" or "failing" or "bad" or "critical")
        {
            return FactValue.FromString("FAILED");
        }

        return FactValue.FromString("UNKNOWN");
    }
}

/// <summary>
/// Clamps a percentage value (float64) to [0.0, 100.0].
/// Accepts multiple patterns — CPU%, container CPU%, battery charge%, SMART wear%
/// all use the same clamp logic.
/// NaN and Inf are clamped to 0.0.
/// </summary>
public sealed class ClampPercentNormalizer : INormalizer
{
    public ClampPercentNormalizer(IReadOnlyList<string>? patterns = null)
    {
        AttributePathPatterns = patterns;
    }

    public IReadOnlyList<string> AttributePathPatterns => field ?? [];

    public FactValue? Normalize(FactValue raw)
    {
        double? val = raw.AsDouble();
        if (val is null)
        {
            return null;
        }

        if (double.IsNaN(val.Value) || double.IsInfinity(val.Value))
        {
            return FactValue.FromDouble(0.0);
        }

        return FactValue.FromDouble(Math.Clamp(val.Value, 0.0, 100.0));
    }
}

/// <summary>
/// Ensures a non-negative bytes value. Accepts multiple patterns.
/// Rejects negative longs (signed overflow). When
/// <paramref>
///     <name>rejectZero</name>
/// </paramref>
/// is true, also rejects zero — use for total-capacity fields (total memory,
/// total disk size) where zero would poison downstream derived percent calculations.
/// </summary>
public sealed class NonNegativeBytesNormalizer : INormalizer
{
    private readonly bool _rejectZero;

    public NonNegativeBytesNormalizer(
        IReadOnlyList<string>? patterns = null,
        bool rejectZero = false
    )
    {
        AttributePathPatterns = patterns;
        _rejectZero = rejectZero;
    }

    public IReadOnlyList<string> AttributePathPatterns => field ?? [];

    public FactValue? Normalize(FactValue raw)
    {
        long? val = raw.AsLong();
        if (val is null)
        {
            return null;
        }

        if (val.Value < 0)
        {
            return null;
        }

        if (_rejectZero && val.Value == 0)
        {
            return null;
        }

        return raw;
    }
}

/// <summary>
/// Normalizes disk type strings to canonical lowercase values:
/// "hdd", "ssd", "nvme", "virtual", or "unknown".
/// </summary>
public sealed class DiskTypeNormalizer : INormalizer
{
    public IReadOnlyList<string> AttributePathPatterns => [FactPaths.DiskType];

    public FactValue? Normalize(FactValue raw)
    {
        string? str = raw.AsString();
        if (str is null)
        {
            return null;
        }

        string lower = str.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(lower))
        {
            return null;
        }

        string canonical = lower switch
        {
            "hdd" or "hard disk drive" or "rotational" or "spinning" => "hdd",
            "ssd" or "solid state drive" or "solid-state" or "flash" => "ssd",
            "nvme" or "nvm express" or "nvm" or "m.2 nvme" => "nvme",
            "virtual" or "lvm" or "dm" or "loop" or "ram" => "virtual",
            _ => lower,
        };
        return FactValue.FromString(canonical);
    }
}

/// <summary>
/// Canonicalizes filesystem type strings to their conventional written form.
/// Cross-platform sources disagree on case for the exact same filesystem: .NET's
/// <c>DriveInfo.DriveFormat</c> reports Windows filesystems upper-case ("NTFS",
/// "FAT32") while Linux/macOS collectors report lower-case ("ext4", "apfs").
/// Each filesystem has its own idiomatic casing — there's no single rule — so
/// this maps known variants to their canonical form rather than forcing one
/// case. A type we don't recognize is trimmed and passed through with its
/// original casing intact; we don't guess a "corrected" case for it.
/// </summary>
public sealed class FsTypeNormalizer : INormalizer
{
    public IReadOnlyList<string> AttributePathPatterns => [FactPaths.FsType];

    public FactValue? Normalize(FactValue raw)
    {
        string? str = raw.AsString();
        if (str is null)
        {
            return null;
        }

        string trimmed = str.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return FactValue.FromString(Canonical.TryGetValue(trimmed, out string? canonical) ? canonical : trimmed);
    }

    private static readonly Dictionary<string, string> Canonical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ntfs"] = "NTFS",
        ["fat32"] = "FAT32",
        ["fat16"] = "FAT16",
        ["fat"] = "FAT",
        ["exfat"] = "exFAT",
        ["apfs"] = "APFS",
        ["hfs+"] = "HFS+",
        ["hfsplus"] = "HFS+",
        ["ext2"] = "ext2",
        ["ext3"] = "ext3",
        ["ext4"] = "ext4",
        ["xfs"] = "xfs",
        ["btrfs"] = "btrfs",
        ["zfs"] = "zfs",
        ["vfat"] = "vfat",
        ["tmpfs"] = "tmpfs",
        ["squashfs"] = "squashfs",
        ["overlay"] = "overlay",
        ["overlay2"] = "overlay2",
        ["iso9660"] = "iso9660",
        ["nfs"] = "NFS",
        ["nfs4"] = "NFS4",
        ["cifs"] = "CIFS",
        ["smb"] = "SMB",
    };
}