using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JMW.Agent.Common.Serialization;

public class IPAddressWithSubnetConverter : JsonConverter<(IPAddress, int)>
{
    public override (IPAddress, int) Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var originalValue = reader.GetString();
        if (originalValue is null)
        {
            throw new InvalidOperationException($"Subnet ({originalValue}) not in a valid format.");
        }

        var prefixLengthDividerIndex = originalValue.IndexOf('/');

        if (prefixLengthDividerIndex == -1)
        {
            throw new InvalidOperationException($"Subnet ({originalValue}) not in a valid format.");
        }
        else
        {
            var address = originalValue[..prefixLengthDividerIndex];
            var prefix = originalValue[(prefixLengthDividerIndex + 1)..];

            if (!IPAddress.TryParse(address, out var addressValue))
            {
                throw new InvalidOperationException($"Subnet ({originalValue}) not in a valid format.");
            }

            if (!int.TryParse(prefix, out var prefixValue))
            {
                throw new InvalidOperationException($"Subnet ({originalValue}) not in a valid format.");
            }

            return (addressValue, prefixValue);
        }
    }

    public override void Write(Utf8JsonWriter writer, (IPAddress, int) value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"{value.Item1}/{value.Item2}");
}
