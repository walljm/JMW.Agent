-- D34 cutover: backfill existing MAC columns to the canonical bare 12-hex lowercase form, so the
-- read-time regex joins (that stripped separators on every query) can be replaced with a direct
-- equality against device_fingerprints.fp_value. Going forward the server already writes bare hex
-- (MacValueNormalizer for values, KeyNormalization for the DHCP-lease MAC key); this only converts
-- rows written before that landed.
--
-- Only rows whose stripped value is exactly 12 hex are touched (guards against obscured MACs and
-- any malformed value — those never resolved via the old regex path either, since it filtered on
-- length = 12 / never matched a 12-hex fingerprint). Idempotent: re-running is a no-op.

UPDATE jmwdiscovery.proj_device_arp
SET
    mac = lower(regexp_replace(mac, '[^0-9a-f]', '', 'gi'))
WHERE
      mac IS NOT NULL
  AND length(regexp_replace(mac, '[^0-9a-f]', '', 'gi')) = 12
  AND mac <> lower(regexp_replace(mac, '[^0-9a-f]', '', 'gi'));

UPDATE jmwdiscovery.proj_discovered
SET
    mac = lower(regexp_replace(mac, '[^0-9a-f]', '', 'gi'))
WHERE
      mac IS NOT NULL
  AND length(regexp_replace(mac, '[^0-9a-f]', '', 'gi')) = 12
  AND mac <> lower(regexp_replace(mac, '[^0-9a-f]', '', 'gi'));

UPDATE jmwdiscovery.proj_dhcp_leases
SET
    lease = lower(regexp_replace(lease, '[^0-9a-f]', '', 'gi'))
WHERE
      lease IS NOT NULL
  AND length(regexp_replace(lease, '[^0-9a-f]', '', 'gi')) = 12
  AND lease <> lower(regexp_replace(lease, '[^0-9a-f]', '', 'gi'));

UPDATE jmwdiscovery.proj_dhcp_local_leases
SET
    lease = lower(regexp_replace(lease, '[^0-9a-f]', '', 'gi'))
WHERE
      lease IS NOT NULL
  AND length(regexp_replace(lease, '[^0-9a-f]', '', 'gi')) = 12
  AND lease <> lower(regexp_replace(lease, '[^0-9a-f]', '', 'gi'));
