using System.Text.Json;

namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Safe scalar accessors for <see cref="JsonElement" />, re-declared per-collector (review D16).
/// Each checks <see cref="JsonElement.ValueKind" /> before calling the typed getter, so a
/// property that exists but isn't the expected JSON type returns the fallback instead of
/// throwing <see cref="InvalidOperationException" /> — the bug in the pre-existing non-checked
/// copies (they called <c>GetString()</c>/<c>GetInt32()</c>/<c>GetInt64()</c> directly).
/// </summary>
public static class JsonElementExtensions
{
    public static string? GetStr(this JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    public static int GetInt(this JsonElement element, string propertyName, int fallback = 0) =>
        element.TryGetProperty(propertyName, out JsonElement prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt32()
            : fallback;

    public static long GetLong(this JsonElement element, string propertyName, long fallback = 0) =>
        element.TryGetProperty(propertyName, out JsonElement prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt64()
            : fallback;
}