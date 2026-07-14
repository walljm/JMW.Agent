using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JMW.Discovery.Core;

// ── FactJsonConverter ─────────────────────────────────────────────────────────
//
// Wire format:
//   { "id": "Device[r1].Interface[eth0].Speed", "value": <FactValue>,
//     "collectedAt": "2026-06-04T00:00:00Z", "source": 107 }
//
// "source" is the FactSource ordinal (a number, not its name) -- cheaper to transmit
// across thousands of facts per batch. It's optional on read (older agent builds
// don't send it) and defaults to FactSource.Unknown; an out-of-range or missing
// value is not an error.

public sealed class FactJsonConverter : JsonConverter<Fact>
{
    public override Fact Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? id = null;
        FactValue value = FactValue.Null;
        DateTimeOffset collected = default;
        FactSource source = FactSource.Unknown;

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of object.");
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name.");
            }

            string propName = reader.GetString() ?? throw new JsonException("Expected non-null property name.");
            reader.Read();

            switch (propName)
            {
                case "id":
                    id = reader.GetString();
                    break;
                case "value":
                    value = JsonSerializer.Deserialize<FactValue>(ref reader, options);
                    break;
                case "collectedAt":
                    collected = reader.GetDateTimeOffset();
                    break;
                case "source":
                    source = reader.TryGetUInt16(out ushort sourceOrdinal)
                        ? (FactSource)sourceOrdinal
                        : FactSource.Unknown;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (id is null)
        {
            throw new JsonException("Missing required property 'id'.");
        }

        // Route through Create() so derived fields (AttributePath, KeyValuesJson, etc.)
        // are computed from the deserialized Id — no raw struct init.
        return Fact.Create(id, value, collected) with { Source = source };
    }

    public override void Write(Utf8JsonWriter writer, Fact value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WritePropertyName("value");
        JsonSerializer.Serialize(writer, value.Value, options);
        writer.WriteString("collectedAt", value.CollectedAt);
        writer.WriteNumber("source", (ushort)value.Source);
        writer.WriteEndObject();
    }
}

// ── FactValueJsonConverter ────────────────────────────────────────────────────
//
// Wire format:
//   { "kind": "IPv4Address", "value": "192.168.1.1" }
//
// All values are serialized as strings regardless of the internal storage kind.
// The "kind" discriminator drives deserialization.

public sealed class FactValueJsonConverter : JsonConverter<FactValue>
{
    public override FactValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return FactValue.Null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of object.");
        }

        FactValueKind kind = FactValueKind.Null;
        string? raw = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name.");
            }

            string propName = reader.GetString() ?? throw new JsonException("Expected non-null property name.");
            reader.Read();

            switch (propName)
            {
                case "kind":
                    if (!Enum.TryParse(reader.GetString(), out kind))
                    {
                        throw new JsonException($"Unknown FactValueKind: {reader.GetString()}");
                    }

                    break;
                case "value":
                    raw = reader.GetString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return Deserialize(kind, raw);
    }

    private static FactValue Deserialize(FactValueKind kind, string? raw)
    {
        if (kind is FactValueKind.Null)
        {
            return FactValue.Null;
        }

        if (kind is FactValueKind.String)
        {
            return FactValue.FromString(raw ?? string.Empty);
        }

        string value = raw ?? throw new JsonException($"Expected non-null value for FactValueKind.{kind}.");
        return kind switch
        {
            FactValueKind.Long => FactValue.FromLong(long.Parse(value)),
            FactValueKind.Double => FactValue.FromDouble(double.Parse(value)),
            FactValueKind.Bool => FactValue.FromBool(bool.Parse(value)),
            FactValueKind.DateTimeOffset => FactValue.FromDateTimeOffset(DateTimeOffset.Parse(value)),
            FactValueKind.TimeSpan => FactValue.FromTimeSpan(TimeSpan.Parse(value)),
            FactValueKind.IPv4Address => FactValue.FromIPAddress(IPAddress.Parse(value)),
            FactValueKind.IPv6Address => FactValue.FromIPAddress(IPAddress.Parse(value)),
            FactValueKind.IPPrefix => FactValue.FromIPNetwork(IPNetwork.Parse(value)),
            FactValueKind.MacAddress => DeserializeMac(value),
            _ => throw new JsonException($"Unhandled FactValueKind: {kind}"),
        };
    }

    private static FactValue DeserializeMac(string raw)
    {
        // Accept XX:XX:XX:XX:XX:XX or XX-XX-XX-XX-XX-XX
        string[] parts = raw.Split(raw.Contains(':') ? ':' : '-');
        if (parts.Length != 6)
        {
            throw new JsonException($"Invalid MAC address: {raw}");
        }

        long mac = 0;
        foreach (string part in parts)
        {
            mac = (mac << 8) | Convert.ToByte(part, 16);
        }

        return FactValue.FromMacAddress(mac);
    }

    public override void Write(Utf8JsonWriter writer, FactValue value, JsonSerializerOptions options)
    {
        if (value.Kind == FactValueKind.Null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind.ToString());
        writer.WriteString("value", value.ToString());
        writer.WriteEndObject();
    }
}