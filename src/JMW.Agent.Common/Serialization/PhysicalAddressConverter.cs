using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JMW.Agent.Common.Serialization;

public class PhysicalAddressConverter : JsonConverter<PhysicalAddress>
{
    public override PhysicalAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        PhysicalAddress.Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, PhysicalAddress value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
