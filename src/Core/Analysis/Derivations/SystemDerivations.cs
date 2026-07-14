namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Computes MemUsedPercent (MemUsedBytes / MemTotalBytes * 100.0) for each device.
/// Scope is inferred from the common list dimensions of the two input patterns:
/// Device[].System.MemUsedBytes ∩ Device[].System.MemTotalBytes → [Device]
/// One derivation instance runs per device.
/// </summary>
public sealed class MemoryUsedPercentDerivation() : BinaryDerivation(
    FactPaths.SystemMemUsedBytes,
    FactPaths.SystemMemTotalBytes,
    FactPaths.Derived.SystemMemUsedPercent,
    extract: f => f.Value.AsLong(),
    combine: (used, total) => total == 0 ? null : used / total * 100.0
);