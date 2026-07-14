namespace JMW.Discovery.Core.Analysis;

/// <summary>
/// Computes one or more facts from a set of input facts that share a common scope.
/// Scope is inferred automatically from the input patterns:
/// inputs = ["Device[].Interface[].InBytes", "Device[].Interface[].OutBytes"]
/// common list dims = {Device, Interface}
/// → one derivation instance per (device, interface) pair
/// inputs = ["Device[].Interface[].Name", "Device[].Vlan[].Id"]
/// common list dims = {Device}
/// → one derivation instance per device, receiving all its interface names
/// and all its VLAN IDs together
/// Output IDs are declared as explicit attribute_path templates.
/// The engine fills in the scope key when building the actual fact ID:
/// "Device[].Interface[].TotalBytes" + scope{"Device":"r1","Interface":"eth0"}
/// → "Device[r1].Interface[eth0].TotalBytes"
/// Missing inputs: return an empty list — no fact emitted, no error.
/// A fact may be observed on one device and derived on another; there is no
/// "derived" flag — the fact ID is the identity.
/// </summary>
public interface IDerivation
{
    /// <summary>
    /// Attribute path patterns of required input facts.
    /// Scope is inferred from the intersection of list dimensions across these patterns.
    /// e.g. ["Device[].Interface[].InBytes", "Device[].Interface[].OutBytes"]
    /// </summary>
    IReadOnlyList<string> Inputs { get; }

    /// <summary>
    /// Attribute path templates this derivation can produce.
    /// Used by the engine for dependency ordering — must be declared up front.
    /// e.g. ["Device[].Interface[].TotalBytes"]
    /// </summary>
    IReadOnlyList<string> Outputs { get; }

    /// <summary>
    /// Explicit scope override. When null (default), scope is inferred from the
    /// intersection of list dimensions across Inputs — correct for most derivations.
    /// Use an explicit scope only for aggregations that collapse a child dimension
    /// to a parent level, where inference would produce too narrow a scope.
    /// Example: count Interface.Enabled facts per Device.
    /// Inputs = ["Device[].Interface[].Enabled"]   → inferred scope = [Device, Interface]
    /// Scope  = ["Device"]                         → override: group by Device only,
    /// receiving ALL interfaces per device
    /// </summary>
    IReadOnlyList<string>? Scope => null;

    /// <summary>
    /// Computes output facts from a scope-grouped set of input facts.
    /// All facts in <paramref name="scopedFacts" /> share the same scope key values.
    /// Returns an empty list when any required input is absent.
    /// </summary>
    IReadOnlyList<Fact> Derive(IReadOnlyList<Fact> scopedFacts);
}