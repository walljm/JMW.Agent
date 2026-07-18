using System.Text.RegularExpressions;

namespace JMW.Discovery.Core.Analysis.Derivations;

/// <summary>
/// Maps a hardware/model string to its manufacturer using a curated token table plus the Apple
/// hardware-model-identifier pattern. Shared by <see cref="VendorFromModelDerivation" /> (a
/// device's own SMBIOS model) and <see cref="VendorFromDiscoveredModelDerivation" /> (a
/// passively-discovered neighbor's mDNS model).
///
/// Matching is case-insensitive <c>Contains</c> against distinctive product-line names — a
/// "ThinkPad" is always Lenovo, a "Nest" is always Google. Contains (not StartsWith) is
/// deliberate: a vendor token often sits mid-string ("Google Nest Mini" → Google, which a "Nest"
/// prefix would miss). The tradeoff is that a token appearing incidentally inside another word
/// also matches, so every token here must be a distinctive product-line name — a short or common
/// word (e.g. "Air", "Pro") would be far too loose for Contains and must not be added.
/// </summary>
public static class ModelVendor
{
    // Order matters only when a model could contain two tokens; the curated set below is
    // effectively disjoint, so first-match is fine.
    private static readonly (string Token, string Vendor)[] Tokens =
    [
        ("ThinkPad", "Lenovo"),
        ("ThinkCentre", "Lenovo"),
        ("ThinkStation", "Lenovo"),
        ("OptiPlex", "Dell"),
        ("Latitude", "Dell"),
        ("PowerEdge", "Dell"),
        ("EliteBook", "HP"),
        ("Pavilion", "HP"),
        ("LaserJet", "HP"),
        ("Galaxy", "Samsung"),
        ("iPhone", "Apple"),
        ("iPad", "Apple"),
        ("MacBook", "Apple"),
        ("iMac", "Apple"),
        ("Surface", "Microsoft"),
        ("ROG", "ASUS"),
        ("ZenBook", "ASUS"),
        // Passive-discovery / IoT model names (mDNS model=) — distinctive enough for Contains.
        ("Nest", "Google"), // "Nest Audio", "Google Nest Mini", "Nest Hub"
        ("Pixel", "Google"), // "Pixel Tablet", "Pixel 8"
        ("Chromecast", "Google"),
    ];

    // Apple hardware-model identifiers: "<Family><major>,<minor>" (e.g. "Mac15,10", "Mac15,3",
    // "MacBookPro14,2", "iMac21,2"). No other vendor uses the "digits,digits" identifier form and
    // every family root is an Apple product name, so this is a precise Apple signal — it catches
    // the bare "MacNN,N" Studio/notebook identifiers the named tokens above miss.
    private static readonly Regex AppleModelIdentifier = new(
        @"^i?Mac[A-Za-z]*\d+,\d+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
    );

    /// <summary>Returns the manufacturer for a model string, or null when unrecognized.</summary>
    public static string? Resolve(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        string trimmed = model.Trim();
        foreach ((string token, string vendor) in Tokens)
        {
            if (trimmed.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return vendor;
            }
        }

        return AppleModelIdentifier.IsMatch(trimmed) ? "Apple" : null;
    }
}