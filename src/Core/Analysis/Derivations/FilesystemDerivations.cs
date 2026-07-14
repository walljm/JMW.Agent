namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Computes UsedPercent (UsedBytes / TotalBytes * 100.0) for each filesystem on each device.
/// Scope is inferred from the common list dimensions of the two input patterns:
/// Device[].Filesystem[].UsedBytes ∩ Device[].Filesystem[].TotalBytes → [Device, Filesystem]
/// One derivation instance runs per (device, filesystem) pair.
/// </summary>
public sealed class UsedPercentDerivation() : BinaryDerivation(
    FactPaths.FsUsedBytes,
    FactPaths.FsTotalBytes,
    FactPaths.Derived.FsUsedPercent,
    extract: f => f.Value.AsLong(),
    combine: (used, total) => total == 0 ? null : used / total * 100.0
);