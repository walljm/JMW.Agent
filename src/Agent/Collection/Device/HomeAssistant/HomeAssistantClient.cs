using System.Text.Json;

namespace JMW.Discovery.Agent.Collection.Device.HomeAssistant;

/// <summary>One Home Assistant device-registry entry (config/device_registry/list).</summary>
public sealed record HaDevice(
    string Id,
    string? AreaId,
    IReadOnlyList<(string Type, string Value)> Connections,
    IReadOnlyList<(string Domain, string Value)> Identifiers,
    string? Manufacturer,
    string? Model,
    string? ModelId,
    string? Name,
    string? NameByUser,
    string? SwVersion,
    string? HwVersion,
    string? SerialNumber,
    IReadOnlyList<string> Labels,
    string? ViaDeviceId,
    bool Disabled,
    string? EntryType
);

/// <summary>One entity-registry entry (config/entity_registry/list) — just enough to join device ↔ state.</summary>
public sealed record HaEntity(string EntityId, string? DeviceId, string? Platform);

/// <summary>One area-registry entry (config/area_registry/list).</summary>
public sealed record HaArea(string AreaId, string Name);

/// <summary>One entity state (get_states) — attributes kept as raw JSON, read on demand.</summary>
public sealed record HaState(string EntityId, string? State, JsonElement Attributes);

/// <summary>
/// Home Assistant Core WebSocket API client (<c>/api/websocket</c>) — auth handshake plus the
/// four id-correlated command/result round trips this collector needs. One-shot: connect,
/// authenticate, run the round trips, let the caller close. No <c>subscribe_events</c> — this
/// is a poll-driven collector, not a live event listener (see plan §2/§4).
/// </summary>
public sealed class HomeAssistantClient
{
    private readonly IHomeAssistantSocket _socket;
    private int _nextId = 1;

    public HomeAssistantClient(IHomeAssistantSocket socket)
    {
        _socket = socket;
    }

    /// <summary>
    /// Connects and completes the auth handshake. Returns false on <c>auth_invalid</c> (a bad
    /// token — the caller should not retry the same token) rather than throwing, so the
    /// collector can log it distinctly from a transport failure.
    /// </summary>
    public async Task<bool> ConnectAndAuthenticateAsync(Uri wsUri, string token, CancellationToken ct)
    {
        await _socket.ConnectAsync(wsUri, ct);

        string first = await _socket.ReceiveAsync(ct);
        using (JsonDocument doc = JsonDocument.Parse(first))
        {
            string? type = doc.RootElement.GetStr("type");
            if (type != "auth_required")
            {
                throw new InvalidOperationException(
                    $"Expected 'auth_required' as Home Assistant's first message, got '{type}'."
                );
            }
        }

        await _socket.SendAsync(JsonSerializer.Serialize(new { type = "auth", access_token = token }), ct);

        string authResult = await _socket.ReceiveAsync(ct);
        using JsonDocument authDoc = JsonDocument.Parse(authResult);
        return authDoc.RootElement.GetStr("type") == "auth_ok";
    }

    public async Task<IReadOnlyList<HaDevice>> GetDeviceRegistryAsync(CancellationToken ct)
    {
        JsonElement result = await SendCommandAsync("config/device_registry/list", ct);
        List<HaDevice> devices = new(result.GetArrayLength());
        foreach (JsonElement e in result.EnumerateArray())
        {
            devices.Add(
                new HaDevice(
                    Id: e.GetStr("id") ?? "",
                    AreaId: e.GetStr("area_id"),
                    Connections: ReadPairs(e, "connections"),
                    Identifiers: ReadPairs(e, "identifiers"),
                    Manufacturer: e.GetStr("manufacturer"),
                    Model: e.GetStr("model"),
                    ModelId: e.GetStr("model_id"),
                    Name: e.GetStr("name"),
                    NameByUser: e.GetStr("name_by_user"),
                    SwVersion: e.GetStr("sw_version"),
                    HwVersion: e.GetStr("hw_version"),
                    SerialNumber: e.GetStr("serial_number"),
                    Labels: ReadStringArray(e, "labels"),
                    ViaDeviceId: e.GetStr("via_device_id"),
                    Disabled: e.TryGetProperty("disabled_by", out JsonElement db) && db.ValueKind == JsonValueKind.String,
                    EntryType: e.GetStr("entry_type")
                )
            );
        }

        return devices.Where(d => d.Id.Length > 0).ToList();
    }

    public async Task<IReadOnlyList<HaEntity>> GetEntityRegistryAsync(CancellationToken ct)
    {
        JsonElement result = await SendCommandAsync("config/entity_registry/list", ct);
        List<HaEntity> entities = new(result.GetArrayLength());
        foreach (JsonElement e in result.EnumerateArray())
        {
            string? entityId = e.GetStr("entity_id");
            if (entityId is { Length: > 0 })
            {
                entities.Add(new HaEntity(entityId, e.GetStr("device_id"), e.GetStr("platform")));
            }
        }

        return entities;
    }

    public async Task<IReadOnlyList<HaArea>> GetAreaRegistryAsync(CancellationToken ct)
    {
        JsonElement result = await SendCommandAsync("config/area_registry/list", ct);
        List<HaArea> areas = new(result.GetArrayLength());
        foreach (JsonElement e in result.EnumerateArray())
        {
            string? areaId = e.GetStr("area_id");
            string? name = e.GetStr("name");
            if (areaId is { Length: > 0 } && name is { Length: > 0 })
            {
                areas.Add(new HaArea(areaId, name));
            }
        }

        return areas;
    }

    public async Task<IReadOnlyList<HaState>> GetStatesAsync(CancellationToken ct)
    {
        JsonElement result = await SendCommandAsync("get_states", ct);
        List<HaState> states = new(result.GetArrayLength());
        foreach (JsonElement e in result.EnumerateArray())
        {
            string? entityId = e.GetStr("entity_id");
            if (entityId is { Length: > 0 })
            {
                JsonElement attrs = e.TryGetProperty("attributes", out JsonElement a) ? a.Clone() : default;
                states.Add(new HaState(entityId, e.GetStr("state"), attrs));
            }
        }

        return states;
    }

    /// <summary>
    /// Sends one id-correlated command and waits for its matching result. Home Assistant
    /// replies to commands in order for a single connection with no active subscriptions, so a
    /// direct one-shot receive is sufficient — no dispatch table needed for this collector's
    /// sequential, non-subscribing usage.
    /// </summary>
    private async Task<JsonElement> SendCommandAsync(string type, CancellationToken ct)
    {
        int id = _nextId++;
        await _socket.SendAsync(JsonSerializer.Serialize(new { id, type }), ct);

        string raw = await _socket.ReceiveAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(raw);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.Number || idEl.GetInt32() != id)
        {
            throw new InvalidOperationException($"Home Assistant reply id mismatch for command '{type}': {raw}");
        }

        bool success = root.TryGetProperty("success", out JsonElement s) && s.ValueKind == JsonValueKind.True;
        if (!success || !root.TryGetProperty("result", out JsonElement result))
        {
            throw new InvalidOperationException($"Home Assistant command '{type}' failed: {raw}");
        }

        return result.Clone();
    }

    // HA represents both `connections` (2-item arrays: [type, value]) and `identifiers`
    // (2-item arrays: [domain, value]) the same shape — array-of-2-element-arrays.
    private static List<(string, string)> ReadPairs(JsonElement device, string property)
    {
        List<(string, string)> pairs = [];
        if (!device.TryGetProperty(property, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return pairs;
        }

        foreach (JsonElement pair in arr.EnumerateArray())
        {
            if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2)
            {
                JsonElement a = pair[0];
                JsonElement b = pair[1];
                if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String)
                {
                    pairs.Add((a.GetString() ?? "", b.GetString() ?? ""));
                }
            }
        }

        return pairs;
    }

    private static List<string> ReadStringArray(JsonElement device, string property)
    {
        List<string> values = [];
        if (!device.TryGetProperty(property, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (JsonElement e in arr.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
            {
                values.Add(s);
            }
        }

        return values;
    }
}
