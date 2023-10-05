using System.Text.Json;
using System.Text.Json.Serialization;

namespace JMW.Agent.Common.Serialization;

public static class SystemTextJsonSerializerSettingsProvider
{
    private const int DefaultMaxDepth = 32;

    public static readonly IPAddressConverter IPAddressConverterInstance = new();
    public static readonly IPAddressWithSubnetConverter IPAddressWithSubnetConverterInstance = new();
    public static readonly PhysicalAddressConverter PhysicalAddressConverterInstance = new();

    private static JsonSerializerOptions? defaultOptions;
    public static JsonSerializerOptions Default => defaultOptions ??= Create();

    private static JsonSerializerOptions? indentedOptions;
    public static JsonSerializerOptions Indented => indentedOptions ??= new(Default) { WriteIndented = true };

    public static void Apply(JsonSerializerOptions options)
    {
        options.MaxDepth = DefaultMaxDepth;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;

        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(IPAddressConverterInstance);
        options.Converters.Add(IPAddressWithSubnetConverterInstance);
        options.Converters.Add(PhysicalAddressConverterInstance);
        options.Converters.Add(new JsonStringEnumConverter());
    }

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        Apply(options);

        return options;
    }
}
