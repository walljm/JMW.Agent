namespace JMW.Discovery.Agent.Collection;

/// <summary>
/// Parse-with-default accessors for a device target's string-keyed properties (review D17): the
/// <c>TryGetValue + T.TryParse + default</c> shape was re-declared per numeric type across the
/// Ssh/Bacnet/Snmp/Modbus device collectors. An absent or unparseable value falls back to
/// defaultValue in every overload — never throws.
/// </summary>
public static class DevicePropertiesExtensions
{
    public static int GetInt(this IReadOnlyDictionary<string, string> properties, string key, int defaultValue) =>
        properties.TryGetValue(key, out string? value) && int.TryParse(value, out int parsed) ? parsed : defaultValue;

    public static ushort GetUShort(this IReadOnlyDictionary<string, string> properties, string key, ushort defaultValue) =>
        properties.TryGetValue(key, out string? value) && ushort.TryParse(value, out ushort parsed)
            ? parsed
            : defaultValue;

    public static byte GetByte(this IReadOnlyDictionary<string, string> properties, string key, byte defaultValue) =>
        properties.TryGetValue(key, out string? value) && byte.TryParse(value, out byte parsed) ? parsed : defaultValue;
}