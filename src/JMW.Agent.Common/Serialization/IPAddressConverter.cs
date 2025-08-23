using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JMW.Agent.Common.Serialization;

public sealed class IPAddressConverter : JsonConverter<IPAddress>
{
    public override IPAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        IPAddress.Parse(reader.GetString() ?? throw new InvalidOperationException($"IP has no value."));

    public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
