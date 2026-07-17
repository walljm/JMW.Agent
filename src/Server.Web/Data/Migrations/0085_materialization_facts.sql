-- Narrow identity-fact projection (docs/plans/architecture-identity-facts.md). Fact-shaped
-- current-value table for materializer-only identity signals — adding a new scanner
-- fingerprint becomes a data change (one more attribute_path routed here), not a migration.
-- Phase 1 of the staged plan (§7): this table is populated by dual write alongside the
-- existing proj_discovered columns, which stay in place until Phase 3 retires them.

CREATE TABLE IF NOT EXISTS materialization_facts (
    device         TEXT        NOT NULL,
    -- Non-device dimension keys, path order, '\0'-joined; '' for device-scoped paths.
    -- For Device[].Discovered[].* paths (the only routing today) this is the neighbor IP.
    entity_key     TEXT        NOT NULL DEFAULT '',
    attribute_path TEXT        NOT NULL, -- full FactPaths template, e.g. 'Device[].Discovered[].SsdpUuid'
    value          TEXT        NOT NULL,
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (device, entity_key, attribute_path)
);

-- Fingerprint lookups ("which rows carry ssdp_uuid = X") and new-since-pass anti-joins.
CREATE INDEX IF NOT EXISTS materialization_facts_path_value_idx
    ON materialization_facts (attribute_path, value);

-- Retention sweep (same updated_at-staleness model as proj_discovered).
CREATE INDEX IF NOT EXISTS materialization_facts_updated_idx
    ON materialization_facts (updated_at);

INSERT INTO retention_policies
(
      table_name
    , category
    , stale_after
    , notes
)
VALUES
(
    'materialization_facts'
    , 'network'
    , INTERVAL '7 days'
    , 'Discovered-neighbor identity signals (serials/UUIDs/host keys); same lifetime as proj_discovered'
) ON CONFLICT (TABLE_NAME) DO NOTHING;

-- Backfill from the eleven proj_discovered columns this plan moves. One arm per column;
-- NULL values are never rows in the narrow table (a fact that was never observed isn't a
-- fact-with-empty-value).
INSERT INTO materialization_facts (device, entity_key, attribute_path, value, updated_at)
SELECT device, discovered, 'Device[].Discovered[].OnvifSerial', onvif_serial, updated_at
FROM proj_discovered WHERE onvif_serial IS NOT NULL
ON CONFLICT (device, entity_key, attribute_path) DO NOTHING;

INSERT INTO materialization_facts (device, entity_key, attribute_path, value, updated_at)
SELECT device, discovered, 'Device[].Discovered[].RokuSerial', roku_serial, updated_at
FROM proj_discovered WHERE roku_serial IS NOT NULL
ON CONFLICT (device, entity_key, attribute_path) DO NOTHING;

INSERT INTO materialization_facts (device, entity_key, attribute_path, value, updated_at)
SELECT device, discovered, 'Device[].Discovered[].SnmpSerial', snmp_serial, updated_at
FROM proj_discovered WHERE snmp_serial IS NOT NULL
ON CONFLICT (device, entity_key, attribute_path) DO NOTHING;

INSERT INTO materialization_facts (device, entity_key, attribute_path, value, updated_at)
SELECT device, discovered, 'Device[].Discovered[].SsdpUuid', ssdp_uuid, updated_at
FROM proj_discovered WHERE ssdp_uuid IS NOT NULL
ON CONFLICT (device, entity_key, attribute_path) DO NOTHING;

INSERT INTO materialization_facts (device, entity_key, attribute_path, value, updated_at)
SELECT device, discovered, 'Device[].Discovered[].WsdUuid', wsd_uuid, updated_at
FROM proj_discovered WHERE wsd_uuid IS NOT NULL
ON CONFLICT (device, entity_key, attribute_path) DO NOTHING;

INSERT INTO materialization_facts (device, entity_key, attribute_path, value, updated_at)
SELECT device, discovered, 'Device[].Discovered[].HueBridgeId', hue_bridge_id, updated_at
FROM proj_discovered WHERE hue_bridge_id IS NOT NULL
ON CONFLICT (device, entity_key, attribute_path) DO NOTHING;

INSERT INTO materialization_facts (device, entity_key, attribute_path, value, updated_at)
SELECT device, discovered, 'Device[].Discovered[].OnvifHardwareId', onvif_hardware_id, updated_at
FROM proj_discovered WHERE onvif_hardware_id IS NOT NULL
ON CONFLICT (device, entity_key, attribute_path) DO NOTHING;

INSERT INTO materialization_facts (device, entity_key, attribute_path, value, updated_at)
SELECT device, discovered, 'Device[].Discovered[].CastId', cast_id, updated_at
FROM proj_discovered WHERE cast_id IS NOT NULL
ON CONFLICT (device, entity_key, attribute_path) DO NOTHING;

INSERT INTO materialization_facts (device, entity_key, attribute_path, value, updated_at)
SELECT device, discovered, 'Device[].Discovered[].DeviceType', device_type, updated_at
FROM proj_discovered WHERE device_type IS NOT NULL
ON CONFLICT (device, entity_key, attribute_path) DO NOTHING;

INSERT INTO materialization_facts (device, entity_key, attribute_path, value, updated_at)
SELECT device, discovered, 'Device[].Discovered[].Os', os, updated_at
FROM proj_discovered WHERE os IS NOT NULL
ON CONFLICT (device, entity_key, attribute_path) DO NOTHING;

INSERT INTO materialization_facts (device, entity_key, attribute_path, value, updated_at)
SELECT device, discovered, 'Device[].Discovered[].SshHostKey', ssh_host_key, updated_at
FROM proj_discovered WHERE ssh_host_key IS NOT NULL
ON CONFLICT (device, entity_key, attribute_path) DO NOTHING;
