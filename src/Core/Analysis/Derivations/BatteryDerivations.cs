namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Computes HealthPercent (CurrentCapacityWh / DesignCapacityWh * 100.0) for each device's battery.
/// Scope is inferred from the common list dimensions of the two input patterns:
/// Device[].Battery.CurrentCapacityWh ∩ Device[].Battery.DesignCapacityWh → [Device]
/// One derivation instance runs per device.
/// </summary>
public sealed class BatteryHealthDerivation() : BinaryDerivation(
    FactPaths.BatteryCurrentCapWh,
    FactPaths.BatteryDesignCapWh,
    FactPaths.Derived.BatteryHealthPercent,
    extract: f => f.Value.AsDouble(),
    combine: (current, design) => design == 0.0 ? null : current / design * 100.0
);