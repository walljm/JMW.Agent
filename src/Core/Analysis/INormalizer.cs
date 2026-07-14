namespace JMW.Discovery.Core.Analysis;

/// <summary>
/// Device context available during normalization. Built from facts already in
/// the batch (vendor and OS are collected during identification, before
/// interface/disk/container data). Values may be null if not yet collected.
/// </summary>
public readonly record struct NormalizationContext(
    string? Vendor, // e.g. "arista", "cisco", "juniper" (raw, pre-normalization)
    string? OsFamily, // e.g. "linux", "eos", "ios-xe"
    string? OsVersion
) // e.g. "4.29.0F", "16.12.4"
{
    /// <summary>Returns true if the vendor string contains the given token (case-insensitive).</summary>
    public bool VendorIs(string token) => Vendor?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Returns true if the OS family contains the given token (case-insensitive).</summary>
    public bool OsFamilyIs(string token) => OsFamily?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// A pure, stateless value transform.
/// The basic overload <c>Normalize(raw)</c> is context-free — no vendor or OS
/// knowledge required. The default implementation of the context-aware overload
/// delegates to the basic one, so normalizers that don't need context only
/// implement the single-argument method.
/// Normalizers that benefit from vendor/OS context override
/// <c>Normalize(raw, ctx)</c>. The engine always calls the context-aware version.
/// </summary>
public interface IValueTransform
{
    /// <summary>
    /// Context-free transform. Implement this unless you need vendor/OS context.
    /// </summary>
    FactValue? Normalize(FactValue raw);

    /// <summary>
    /// Context-aware transform. Default delegates to the context-free version.
    /// Override when vendor or OS changes the normalization logic.
    /// </summary>
    FactValue? Normalize(FactValue raw, NormalizationContext ctx) => Normalize(raw);
}

/// <summary>
/// Registered normalizer: one or more attribute_path patterns plus a transform.
/// See <see cref="IValueTransform" /> for context-aware normalization.
/// </summary>
public interface INormalizer : IValueTransform
{
    IReadOnlyList<string> AttributePathPatterns { get; }
}

/// <summary>
/// Applies a sequence of <see cref="IValueTransform" /> steps in order.
/// Context is forwarded to every step — steps that care about vendor/OS will use it.
/// Short-circuits on null: if any step returns null the fact is dropped.
/// </summary>
public sealed class NormalizerPipeline : INormalizer
{
    private readonly IReadOnlyList<IValueTransform> _steps;
    private readonly IReadOnlyList<string> _patterns;

    public NormalizerPipeline(
        IReadOnlyList<string> patterns,
        IReadOnlyList<IValueTransform> steps
    )
    {
        _patterns = patterns;
        _steps = steps;
    }

    public IReadOnlyList<string> AttributePathPatterns => _patterns;

    public FactValue? Normalize(FactValue raw) => Normalize(raw, default);

    public FactValue? Normalize(FactValue raw, NormalizationContext ctx)
    {
        FactValue? current = raw;
        foreach (IValueTransform step in _steps)
        {
            if (current is null)
            {
                return null;
            }

            current = step.Normalize(current.Value, ctx);
        }

        return current;
    }
}