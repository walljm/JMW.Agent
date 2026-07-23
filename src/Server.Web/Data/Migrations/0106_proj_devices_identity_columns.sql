-- Context-derivation finals on proj_devices (docs/plans/context-derivations.md §3.2): the
-- resolved "best current value" identity picks per device — hostname, friendly_name, mac, ip —
-- written by ContextDerivationEngine as ordinary Derived.Identity* facts through the projection
-- router, landing beside the existing derivation finals (vendor/kind/model). Each is a set-based
-- pick over state the per-batch derivation engine structurally cannot see (observer-keyed
-- proj_discovered/proj_device_arp rows joined by MAC, the device_fingerprints registry,
-- raw-SQL-written last_seen_ip). Deliberately NOT on proj_systems: those columns are these
-- derivations' raw INPUTS, and raw-vs-derived stay separated (same rule as VendorGuess vs
-- VendorCanonical).
--
-- Reports use proj_devices as the DRIVING table for sorts — an ORDER BY on a LEFT-JOINed
-- table's column is never index-satisfiable (verified via EXPLAIN, see the plan doc) — so each
-- sortable column gets an expression index matching the report's exact ORDER BY form, device
-- trailing as the keyset tiebreaker. vendor was already here; it gains the same treatment.
--
-- Columns are hand-migrated (not left to ProjectionSchemaService's additive DDL) because
-- integration tests apply only the migration chain, and indexes are migration-owned by
-- convention.
ALTER TABLE proj_devices ADD COLUMN IF NOT EXISTS hostname text;
ALTER TABLE proj_devices ADD COLUMN IF NOT EXISTS friendly_name text;
ALTER TABLE proj_devices ADD COLUMN IF NOT EXISTS mac text;
ALTER TABLE proj_devices ADD COLUMN IF NOT EXISTS ip text;

CREATE INDEX IF NOT EXISTS proj_devices_hostname_sort_idx
    ON proj_devices ((coalesce(hostname, '')), device);

CREATE INDEX IF NOT EXISTS proj_devices_friendly_name_sort_idx
    ON proj_devices ((coalesce(friendly_name, '')), device);

CREATE INDEX IF NOT EXISTS proj_devices_mac_sort_idx
    ON proj_devices ((coalesce(mac, '')), device);

-- ip_sort_key (0041, IMMUTABLE) makes lexical order match numeric IP order.
CREATE INDEX IF NOT EXISTS proj_devices_ip_sort_idx
    ON proj_devices ((ip_sort_key(ip)), device);

CREATE INDEX IF NOT EXISTS proj_devices_vendor_sort_idx
    ON proj_devices ((coalesce(vendor, '')), device);
