-- Device-registry entries reported by a Home Assistant "home-assistant-devices" service
-- target (HomeAssistantDeviceCollector). Key dimensions: Service, HaDevice (HA's own
-- device-registry entry id, stable across restarts). DiscoveryMaterializer reads this table
-- to promote each entry into its own Device[] row — see MaterializeHomeAssistantDevicesAsync.

CREATE TABLE IF NOT EXISTS jmwdiscovery.proj_service_ha_devices (
    service           TEXT NOT NULL,
    hadevice          TEXT NOT NULL,
    mac               TEXT,
    identifiers       TEXT,
    manufacturer      TEXT,
    model             TEXT,
    model_id          TEXT,
    hw_version        TEXT,
    sw_version        TEXT,
    name              TEXT,
    area_name         TEXT,
    via_device_key    TEXT,
    online            BOOLEAN,
    battery_percent   BIGINT,
    update_available  BOOLEAN,
    latest_version    TEXT,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (service, hadevice)
);
