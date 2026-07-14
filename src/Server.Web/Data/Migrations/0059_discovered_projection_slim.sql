-- Slim proj_discovered down to columns with a genuine join/identity/cross-device-query need.
--
-- firmware: written on every ingest (Device[].Discovered[].Firmware), read by nothing — not
-- the materializer promotion pipeline, not any display query. Pure dead write cost. Now
-- surfaced via the "Discovered (Probed)" fact view instead (FactViewLibrary.cs), which reads
-- facts_history directly at render time — no storage cost when nobody's viewing that device.
--
-- connection_medium/band/guest/signal_dbm/tx_rate_mbps/rx_rate_mbps/rx_bytes/tx_bytes/
-- connected_seconds: per-sighting WiFi link telemetry, read only by the Sightings tab (no join,
-- no cross-device filter/sort), and among the highest write-frequency columns in this table
-- (updated on every WiFi scan cycle). Now surfaced via the "Sighting Telemetry" fact view.
ALTER TABLE proj_discovered
    DROP COLUMN firmware,
    DROP COLUMN connection_medium,
    DROP COLUMN band,
    DROP COLUMN guest,
    DROP COLUMN signal_dbm,
    DROP COLUMN tx_rate_mbps,
    DROP COLUMN rx_rate_mbps,
    DROP COLUMN rx_bytes,
    DROP COLUMN tx_bytes,
    DROP COLUMN connected_seconds;
