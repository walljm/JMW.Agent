-- Backfill: canonicalize proj_discovered's serial/UUID columns to the format
-- device_fingerprints.fp_value stores, closing the same anti-join mismatch performance-01 fixed
-- for MAC columns (migration 0045) — SerialValueNormalizer/UuidValueNormalizer write this format
-- for new rows going forward; this only converts rows written before that landed.
-- Idempotent: re-running is a no-op.

-- Serials: fp_value for an unscoped chassis-serial fingerprint is "bare:<lowercased trimmed
-- value>" (FingerprintNormalizer.NormalizeSerial). ONVIF/Roku scanners never emit a vendor-scoped
-- serial (that only happens on the host-collector path, which doesn't write these columns), so
-- "NOT LIKE '%:%'" is just a defensive guard against double-prefixing an unexpected value.
UPDATE jmwdiscovery.proj_discovered
SET onvif_serial = 'bare:' || lower(trim(onvif_serial))
WHERE onvif_serial IS NOT NULL
  AND onvif_serial NOT LIKE 'bare:%'
  AND onvif_serial NOT LIKE '%:%';

UPDATE jmwdiscovery.proj_discovered
SET roku_serial = 'bare:' || lower(trim(roku_serial))
WHERE roku_serial IS NOT NULL
  AND roku_serial NOT LIKE 'bare:%'
  AND roku_serial NOT LIKE '%:%';

-- UUIDs: only touch values that already parse as a UUID (braces optional); canonicalize to the
-- same lowercase hyphenated form FingerprintNormalizer.NormalizeUuid produces. Anything that
-- doesn't parse is left as-is — it never matched a fingerprint before either, and the exact
-- original text isn't worth guessing at.
UPDATE jmwdiscovery.proj_discovered
SET ssdp_uuid = lower(trim(both '{}' from trim(ssdp_uuid))::uuid::text)
WHERE ssdp_uuid IS NOT NULL
  AND trim(both '{}' from trim(ssdp_uuid)) ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
  AND ssdp_uuid <> lower(trim(both '{}' from trim(ssdp_uuid))::uuid::text);

UPDATE jmwdiscovery.proj_discovered
SET wsd_uuid = lower(trim(both '{}' from trim(wsd_uuid))::uuid::text)
WHERE wsd_uuid IS NOT NULL
  AND trim(both '{}' from trim(wsd_uuid)) ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
  AND wsd_uuid <> lower(trim(both '{}' from trim(wsd_uuid))::uuid::text);
