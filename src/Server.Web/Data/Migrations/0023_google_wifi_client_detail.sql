-- Rich per-client detail from the Google Wifi collector.
--
-- Intrinsic device attributes (oui, friendly_name, device_type) are kept on the
-- observation row; the materializer PROMOTES them onto the resolved Device[] on
-- reconstruction. The remaining columns are the sighting/link (this observer's
-- view of the client) and stay on the observation.
ALTER TABLE jmwdiscovery.proj_discovered
    ADD COLUMN if NOT EXISTS oui text,
    ADD COLUMN if NOT EXISTS friendly_name text,
    ADD COLUMN if NOT EXISTS device_type text,
    ADD COLUMN if NOT EXISTS connection_medium text,
    ADD COLUMN if NOT EXISTS band text,
    ADD COLUMN if NOT EXISTS guest boolean,
    ADD COLUMN if NOT EXISTS signal_dbm bigint,
    ADD COLUMN if NOT EXISTS tx_rate_mbps DOUBLE PRECISION,
    ADD COLUMN if NOT EXISTS rx_rate_mbps DOUBLE PRECISION,
    ADD COLUMN if NOT EXISTS rx_bytes bigint,
    ADD COLUMN if NOT EXISTS tx_bytes bigint,
    ADD COLUMN if NOT EXISTS connected_seconds bigint;

-- Advertised services per discovered neighbor (mDNS): a real list dimension keyed
-- by (device, discovered ip, service type) rather than a comma-joined string.
CREATE TABLE if NOT EXISTS jmwdiscovery.proj_discovered_services (
    device
    TEXT
    NOT
    NULL,
    discovered
    TEXT
    NOT
    NULL,
    service
    TEXT
    NOT
    NULL,
    NAME
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 discovered,
                 service
                )
    );

CREATE INDEX if NOT EXISTS proj_discovered_services_service_idx
    ON jmwdiscovery.proj_discovered_services (service);
