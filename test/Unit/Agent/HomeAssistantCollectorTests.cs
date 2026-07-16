using JMW.Discovery.Agent;
using JMW.Discovery.Agent.Collection;
using JMW.Discovery.Agent.Collection.Device;
using JMW.Discovery.Agent.Collection.Device.HomeAssistant;
using JMW.Discovery.Core;

namespace JMW.Discovery.UnitTests.Agent;

public sealed class HomeAssistantCollectorTests
{
    [Fact]
    public void CanCollect_MatchesHomeAssistantType()
    {
        HomeAssistantCollector collector = new(() => new FakeSocket([]));

        Assert.True(collector.CanCollect(new Target { CollectorType = "home-assistant", Endpoint = "https://ha:8123" }));
        Assert.True(collector.CanCollect(new Target { CollectorType = "HOME-ASSISTANT", Endpoint = "https://ha:8123" }));
        Assert.False(collector.CanCollect(new Target { CollectorType = "technitium-dns", Endpoint = "https://ha:8123" }));
    }

    [Fact]
    public async Task Collect_HappyPath_EmitsDeviceAndHealthFacts()
    {
        FakeSocket socket = new(
            [
                AuthRequired,
                AuthOk,
                Result(1, DeviceRegistryJson),
                Result(2, EntityRegistryJson),
                Result(3, AreaRegistryJson),
                Result(4, StatesJson),
            ]
        );
        FakeServiceContext ctx = new();

        List<Fact> facts = (await Collect(socket, ctx)).ToList();

        Assert.True(socket.Disposed);
        Assert.NotNull(ctx.Probe);
        Assert.Equal("home-assistant", ctx.Probe!.ServiceType);

        // ── MAC-bearing device (Hue lamp) — identity + health signals ──
        const string lamp = "Service[svc-1].HomeAssistant.HaDevice[dev-hue-1]";
        Assert.Equal("aa:bb:cc:dd:ee:ff", Value(facts, $"{lamp}.Mac"));
        Assert.Equal("Signify", Value(facts, $"{lamp}.Manufacturer"));
        Assert.Equal("Hue color lamp", Value(facts, $"{lamp}.Model"));
        Assert.Equal("Living Room Lamp", Value(facts, $"{lamp}.Name"));
        Assert.Equal("Living Room", Value(facts, $"{lamp}.AreaName"));
        Assert.Equal(76L, Long(facts, $"{lamp}.BatteryPercent"));
        Assert.Equal(true, Bool(facts, $"{lamp}.Online"));
        Assert.Equal(true, Bool(facts, $"{lamp}.UpdateAvailable"));
        Assert.Equal("1.3.0", Value(facts, $"{lamp}.LatestVersion"));

        // ── Zigbee coordinator/child (no MAC, "zha" identifiers — not allow-listed) ──
        // are both dropped: bare Zigbee/Z-Wave nodes duplicate a device some other
        // integration already reports with a real MAC (or have no reliable per-unit
        // identity at all), so they're excluded by design — see
        // Collect_MacLessDisallowedIdentifierDomain_Skipped.
        Assert.DoesNotContain(facts, f => f.Id.Contains("dev-zha-coord", StringComparison.Ordinal));
        Assert.DoesNotContain(facts, f => f.Id.Contains("dev-zha-bulb", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collect_ServiceEntryType_Skipped()
    {
        const string devices =
            """
            [{"id":"dev-backup","connections":[],"identifiers":[["backup","backup_manager"]],
              "manufacturer":"Home Assistant","model":"Home Assistant Backup","entry_type":"service"}]
            """;
        FakeSocket socket = new(
            [AuthRequired, AuthOk, Result(1, devices), Result(2, "[]"), Result(3, "[]"), Result(4, "[]")]
        );

        List<Fact> facts = (await Collect(socket, new FakeServiceContext())).ToList();

        Assert.DoesNotContain(facts, f => f.Id.Contains("dev-backup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collect_MacLessDisallowedIdentifierDomain_Skipped()
    {
        const string devices =
            """
            [{"id":"dev-google-home","connections":[],
              "identifiers":[["google_home","d360b791-3251-4d85-a06a-7dad9f037f85"]],
              "manufacturer":"Google Home","model":"Nest Audio"}]
            """;
        FakeSocket socket = new(
            [AuthRequired, AuthOk, Result(1, devices), Result(2, "[]"), Result(3, "[]"), Result(4, "[]")]
        );

        List<Fact> facts = (await Collect(socket, new FakeServiceContext())).ToList();

        Assert.DoesNotContain(facts, f => f.Id.Contains("dev-google-home", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ipp", "5837574D3032333100", null)] // 18 hex chars — not a MAC shape, no fallback
    [InlineData("homeassistant_connect_zbt2", "1CDBD45E6F90", "1CDBD45E6F90")] // radio EUI-48 → MAC
    [InlineData("homeassistant_sky_connect", "AABBCCDDEEFF", "AABBCCDDEEFF")]
    [InlineData("homeassistant_yellow", "not-a-mac-serial", null)] // homeassistant_* but non-hex value
    public async Task Collect_MacLessAllowedIdentifierDomain_Included(string domain, string value, string? expectedMac)
    {
        string devices =
            $$"""
            [{"id":"dev-allowed","connections":[],"identifiers":[["{{domain}}","{{value}}"]],
              "manufacturer":"Acme","model":"Widget"}]
            """;
        FakeSocket socket = new(
            [AuthRequired, AuthOk, Result(1, devices), Result(2, "[]"), Result(3, "[]"), Result(4, "[]")]
        );

        List<Fact> facts = (await Collect(socket, new FakeServiceContext())).ToList();

        Assert.Equal($"{domain}:{value}", Value(facts, "Service[svc-1].HomeAssistant.HaDevice[dev-allowed].Identifiers"));

        // A homeassistant_* identifier value shaped like a bare EUI-48 doubles as the device MAC
        // (Nabu Casa radios register identifiers-only, no "mac" connection pair).
        if (expectedMac is null)
        {
            Assert.DoesNotContain(
                facts,
                f => f.Id == "Service[svc-1].HomeAssistant.HaDevice[dev-allowed].Mac"
            );
        }
        else
        {
            Assert.Equal(expectedMac, Value(facts, "Service[svc-1].HomeAssistant.HaDevice[dev-allowed].Mac"));
        }
    }

    [Fact]
    public async Task Collect_ExplicitMacConnection_WinsOverIdentifierFallback()
    {
        string devices =
            """
            [{"id":"dev-zbt","connections":[["mac","1c:db:d4:5e:6f:90"]],
              "identifiers":[["homeassistant_connect_zbt2","FFFFFFFFFFFF"]],
              "manufacturer":"Nabu Casa","model":"Home Assistant Connect ZBT-2"}]
            """;
        FakeSocket socket = new(
            [AuthRequired, AuthOk, Result(1, devices), Result(2, "[]"), Result(3, "[]"), Result(4, "[]")]
        );

        List<Fact> facts = (await Collect(socket, new FakeServiceContext())).ToList();

        Assert.Equal("1c:db:d4:5e:6f:90", Value(facts, "Service[svc-1].HomeAssistant.HaDevice[dev-zbt].Mac"));
    }

    [Theory]
    [InlineData(
        "uuid:f8aad9f9-c736-46a1-84f6-52f81b7a668d::urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "f8aad9f9-c736-46a1-84f6-52f81b7a668d"
    )]
    [InlineData("uuid:f8aad9f9-c736-46a1-84f6-52f81b7a668d", "f8aad9f9-c736-46a1-84f6-52f81b7a668d")]
    public async Task Collect_MacLessUpnpIdentifierWithParseableUuid_Included(string rawValue, string expectedUuid)
    {
        string devices =
            $$"""
            [{"id":"dev-upnp","connections":[],
              "identifiers":[["upnp_serial_number","00000000"],["upnp","{{rawValue}}"]],
              "manufacturer":"Acme","model":"Widget"}]
            """;
        FakeSocket socket = new(
            [AuthRequired, AuthOk, Result(1, devices), Result(2, "[]"), Result(3, "[]"), Result(4, "[]")]
        );

        List<Fact> facts = (await Collect(socket, new FakeServiceContext())).ToList();

        const string dev = "Service[svc-1].HomeAssistant.HaDevice[dev-upnp]";
        Assert.Equal(expectedUuid, Value(facts, $"{dev}.UpnpUuid"));
        // upnp_serial_number alone isn't allow-listed — only the parseable upnp: UUID grants entry.
        Assert.NotNull(Value(facts, $"{dev}.Identifiers"));
    }

    [Fact]
    public async Task Collect_UpnpIdentifierWithUnparseableValue_NotAdmittedOnItsOwn()
    {
        const string devices =
            """
            [{"id":"dev-bad-upnp","connections":[],"identifiers":[["upnp","not-a-uuid"]],
              "manufacturer":"Acme","model":"Widget"}]
            """;
        FakeSocket socket = new(
            [AuthRequired, AuthOk, Result(1, devices), Result(2, "[]"), Result(3, "[]"), Result(4, "[]")]
        );

        List<Fact> facts = (await Collect(socket, new FakeServiceContext())).ToList();

        Assert.DoesNotContain(facts, f => f.Id.Contains("dev-bad-upnp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collect_MacBearingDeviceWithUpnpIdentifier_EmitsBothMacAndUpnpUuid()
    {
        // Mirrors the OnHub router entry actually seen in production: a real MAC, a distinct
        // UUID on "connections" (type upnp — HA's own CONNECTION_UPNP) and a *different*
        // distinct UUID on "identifiers" (domain upnp, fuller USN shape) — routers commonly
        // advertise two separate UPnP root devices (IGD + WPS/Basic) with different UUIDs, so
        // both must survive as their own fingerprint, not just the first one found.
        const string devices =
            """
            [{"id":"dev-onhub","connections":[["mac","70:3a:cb:ea:a7:bc"],
                ["upnp","uuid:3ec53bb9-70db-457d-8721-98f41351f305"]],
              "identifiers":[["upnp_serial_number","00000000"],
                ["upnp","uuid:f8aad9f9-c736-46a1-84f6-52f81b7a668d::urn:schemas-upnp-org:device:InternetGatewayDevice:2"],
                ["upnp_host","192.168.1.1"]],
              "manufacturer":"Google","model":"OnHub","serial_number":null,"labels":["core-network"]}]
            """;
        FakeSocket socket = new(
            [AuthRequired, AuthOk, Result(1, devices), Result(2, "[]"), Result(3, "[]"), Result(4, "[]")]
        );

        List<Fact> facts = (await Collect(socket, new FakeServiceContext())).ToList();

        const string dev = "Service[svc-1].HomeAssistant.HaDevice[dev-onhub]";
        Assert.Equal("70:3a:cb:ea:a7:bc", Value(facts, $"{dev}.Mac"));
        Assert.Equal(
            "3ec53bb9-70db-457d-8721-98f41351f305|f8aad9f9-c736-46a1-84f6-52f81b7a668d",
            Value(facts, $"{dev}.UpnpUuid")
        );
        Assert.Equal("core-network", Value(facts, $"{dev}.Labels"));
    }

    [Fact]
    public async Task Collect_SerialNumberAndMultipleLabels_Emitted()
    {
        const string devices =
            """
            [{"id":"dev-labeled","connections":[["mac","aa:bb:cc:11:22:33"]],"identifiers":[],
              "manufacturer":"Acme","model":"Widget","serial_number":"SN12345",
              "labels":["security","outdoor"]}]
            """;
        FakeSocket socket = new(
            [AuthRequired, AuthOk, Result(1, devices), Result(2, "[]"), Result(3, "[]"), Result(4, "[]")]
        );

        List<Fact> facts = (await Collect(socket, new FakeServiceContext())).ToList();

        const string dev = "Service[svc-1].HomeAssistant.HaDevice[dev-labeled]";
        Assert.Equal("SN12345", Value(facts, $"{dev}.SerialNumber"));
        Assert.Equal("security|outdoor", Value(facts, $"{dev}.Labels"));
    }

    [Fact]
    public async Task Collect_NoToken_ReturnsEmpty()
    {
        HomeAssistantCollector collector = new(() => new FakeSocket([]));

        IReadOnlyList<Fact> facts = await collector.CollectAsync(
            new Target { CollectorType = "home-assistant", Endpoint = "https://ha:8123" }, // no Credentials
            new FakeServiceContext(),
            CancellationToken.None
        );

        Assert.Empty(facts);
    }

    [Fact]
    public async Task Collect_AuthInvalid_ReturnsEmptyAndSendsNoCommands()
    {
        FakeSocket socket = new([AuthRequired, AuthInvalid]);

        IReadOnlyList<Fact> facts = await Collect(socket, new FakeServiceContext());

        Assert.Empty(facts);
        // Only the auth message was sent — no registry/state commands attempted after rejection.
        Assert.Single(socket.Sent);
        Assert.True(socket.Disposed);
    }

    [Fact]
    public async Task Collect_ConnectThrows_ReturnsEmpty()
    {
        FakeSocket socket = new([]) { ThrowOnConnect = true };

        IReadOnlyList<Fact> facts = await Collect(socket, new FakeServiceContext());

        Assert.Empty(facts);
        Assert.True(socket.Disposed);
    }

    [Fact]
    public async Task Collect_DeviceRegistryFails_ReturnsEmpty()
    {
        FakeSocket socket = new([AuthRequired, AuthOk, ResultFail(1)]);

        IReadOnlyList<Fact> facts = await Collect(socket, new FakeServiceContext());

        // The device registry is essential — nothing else is worth collecting without it.
        Assert.Empty(facts);
    }

    [Fact]
    public async Task Collect_EntityRegistryFails_StillEmitsDeviceIdentityFacts()
    {
        FakeSocket socket = new(
            [
                AuthRequired,
                AuthOk,
                Result(1, DeviceRegistryJson),
                ResultFail(2), // entity registry
                Result(3, AreaRegistryJson),
                Result(4, "[]"), // no states reachable without the entity registry join anyway
            ]
        );

        List<Fact> facts = (await Collect(socket, new FakeServiceContext())).ToList();

        const string lamp = "Service[svc-1].HomeAssistant.HaDevice[dev-hue-1]";
        // Device identity still present...
        Assert.Equal("Signify", Value(facts, $"{lamp}.Manufacturer"));
        Assert.Equal("Living Room", Value(facts, $"{lamp}.AreaName"));
        // ...but no health facts, since they're mined by joining entity registry -> states.
        Assert.Null(Value(facts, $"{lamp}.BatteryPercent"));
        Assert.Null(Value(facts, $"{lamp}.Online"));
    }

    [Fact]
    public async Task Collect_DeviceWithNeitherMacNorIdentifiers_Skipped()
    {
        const string devices =
            """
            [{"id":"dev-orphan","connections":[],"identifiers":[],"manufacturer":"Acme","model":"Widget"}]
            """;
        FakeSocket socket = new(
            [AuthRequired, AuthOk, Result(1, devices), Result(2, "[]"), Result(3, "[]"), Result(4, "[]")]
        );

        List<Fact> facts = (await Collect(socket, new FakeServiceContext())).ToList();

        Assert.DoesNotContain(facts, f => f.Id.Contains("dev-orphan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collect_DisabledDevice_Skipped()
    {
        const string devices =
            """
            [{"id":"dev-disabled","connections":[["mac","11:22:33:44:55:66"]],"identifiers":[],
              "manufacturer":"Acme","disabled_by":"user"}]
            """;
        FakeSocket socket = new(
            [AuthRequired, AuthOk, Result(1, devices), Result(2, "[]"), Result(3, "[]"), Result(4, "[]")]
        );

        List<Fact> facts = (await Collect(socket, new FakeServiceContext())).ToList();

        Assert.DoesNotContain(facts, f => f.Id.Contains("dev-disabled", StringComparison.Ordinal));
    }

    // ── Supervisor best-effort secondary fetch ──────────────────────────────────
    // The Supervisor REST API only ever resolves against the internal http://supervisor
    // address (what Docker gives an HA add-on container) — there's no seam to inject a
    // fake HttpClient for it here, and a live call to "http://supervisor" from a test
    // process risks slow/flaky DNS behavior (bare-hostname resolution can trigger mDNS
    // fallback delays on some platforms), so it's deliberately not exercised with
    // SUPERVISOR_TOKEN set. This test only pins the cheap, deterministic half: no env
    // var means the Supervisor fetch is skipped before any network call is attempted,
    // and the mandatory device-registry facts are unaffected either way.

    [Fact]
    public async Task Collect_NoSupervisorToken_SkipsSupervisorButStillEmitsDeviceFacts()
    {
        string? previous = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");
        Environment.SetEnvironmentVariable("SUPERVISOR_TOKEN", null);
        try
        {
            FakeSocket socket = new(
                [AuthRequired, AuthOk, Result(1, DeviceRegistryJson), Result(2, "[]"), Result(3, "[]"), Result(4, "[]")]
            );

            List<Fact> facts = (await Collect(socket, new FakeServiceContext())).ToList();

            Assert.Equal("Signify", Value(facts, "Service[svc-1].HomeAssistant.HaDevice[dev-hue-1].Manufacturer"));
            Assert.Null(Value(facts, "Service[svc-1].HomeAssistant.SupervisorVersion"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPERVISOR_TOKEN", previous);
        }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private const string DeviceRegistryJson =
        """
        [
          {
            "id": "dev-hue-1",
            "area_id": "living_room",
            "connections": [["mac", "aa:bb:cc:dd:ee:ff"]],
            "identifiers": [],
            "manufacturer": "Signify",
            "model": "Hue color lamp",
            "model_id": "LCT007",
            "name": "Living Room Lamp",
            "name_by_user": null,
            "sw_version": "1.2.3",
            "hw_version": null,
            "via_device_id": null,
            "disabled_by": null
          },
          {
            "id": "dev-zha-coord",
            "area_id": null,
            "connections": [],
            "identifiers": [["zha", "00:11:22:33:44:55:66:00"]],
            "manufacturer": "ITead",
            "model": "Sonoff Zigbee Bridge",
            "name": "Zigbee Coordinator",
            "disabled_by": null
          },
          {
            "id": "dev-zha-bulb",
            "area_id": "bedroom",
            "connections": [],
            "identifiers": [["zha", "00:11:22:33:44:55:66:77"]],
            "manufacturer": "IKEA",
            "model": "TRADFRI bulb",
            "name": "Bedroom Bulb",
            "via_device_id": "dev-zha-coord",
            "disabled_by": null
          }
        ]
        """;

    private const string EntityRegistryJson =
        """
        [
          {"entity_id": "sensor.living_room_lamp_battery", "device_id": "dev-hue-1", "platform": "hue"},
          {"entity_id": "binary_sensor.living_room_lamp_connectivity", "device_id": "dev-hue-1", "platform": "hue"},
          {"entity_id": "update.living_room_lamp_firmware", "device_id": "dev-hue-1", "platform": "hue"},
          {"entity_id": "sensor.bedroom_bulb_battery", "device_id": "dev-zha-bulb", "platform": "zha"}
        ]
        """;

    private const string AreaRegistryJson =
        """
        [
          {"area_id": "living_room", "name": "Living Room"},
          {"area_id": "bedroom", "name": "Bedroom"}
        ]
        """;

    private const string StatesJson =
        """
        [
          {"entity_id": "sensor.living_room_lamp_battery", "state": "76",
           "attributes": {"device_class": "battery", "unit_of_measurement": "%"}},
          {"entity_id": "binary_sensor.living_room_lamp_connectivity", "state": "on",
           "attributes": {"device_class": "connectivity"}},
          {"entity_id": "update.living_room_lamp_firmware", "state": "on",
           "attributes": {"latest_version": "1.3.0", "installed_version": "1.2.3"}},
          {"entity_id": "sensor.bedroom_bulb_battery", "state": "42",
           "attributes": {"device_class": "battery"}}
        ]
        """;

    private const string AuthRequired = """{"type":"auth_required"}""";
    private const string AuthOk = """{"type":"auth_ok"}""";
    private const string AuthInvalid = """{"type":"auth_invalid"}""";

    private static string Result(int id, string resultJson) =>
        $$"""{"id":{{id}},"type":"result","success":true,"result":{{resultJson}} }""";

    private static string ResultFail(int id) =>
        $$"""{"id":{{id}},"type":"result","success":false,"error":"nope"}""";

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Task<IReadOnlyList<Fact>> Collect(FakeSocket socket, IServiceCollectionContext ctx)
    {
        HomeAssistantCollector collector = new(() => socket);
        return collector.CollectAsync(
            new Target
            {
                CollectorType = "home-assistant",
                Endpoint = "https://ha.home:8123",
                Credentials = new ApiTokenCredentials { Token = "tok" },
            },
            ctx,
            CancellationToken.None
        );
    }

    private static string? Value(IEnumerable<Fact> facts, string id) =>
        facts.FirstOrDefault(fact => fact.Id == id) is { } f ? f.Value.AsString() : null;

    private static long? Long(IEnumerable<Fact> facts, string id) =>
        facts.FirstOrDefault(fact => fact.Id == id) is { } f ? f.Value.AsLong() : null;

    private static bool? Bool(IEnumerable<Fact> facts, string id) =>
        facts.FirstOrDefault(fact => fact.Id == id) is { } f ? f.Value.AsBool() : null;

    private sealed class FakeSocket : IHomeAssistantSocket
    {
        private readonly Queue<string> _incoming;

        public FakeSocket(IEnumerable<string> incoming)
        {
            _incoming = new Queue<string>(incoming);
        }

        public bool ThrowOnConnect { get; init; }
        public List<string> Sent { get; } = [];
        public bool Disposed { get; private set; }

        public Task ConnectAsync(Uri uri, CancellationToken ct) => ThrowOnConnect
            ? throw new InvalidOperationException("simulated connect failure")
            : Task.CompletedTask;

        public Task SendAsync(string message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task<string> ReceiveAsync(CancellationToken ct) => _incoming.Count > 0
            ? Task.FromResult(_incoming.Dequeue())
            : throw new InvalidOperationException("No more queued messages.");

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeServiceContext : IServiceCollectionContext
    {
        public string AgentId => "agent-1";
        public string? HostDeviceId => null;
        public ServiceProbe? Probe { get; private set; }

        public Task<string> IdentifyServiceAsync(ServiceProbe probe, CancellationToken ct)
        {
            Probe = probe;
            return Task.FromResult("svc-1");
        }
    }
}