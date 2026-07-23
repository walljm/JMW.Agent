-- proj_devices (pdv) DRIVES the query: the identity sort columns live on it, so the
-- keyset WHERE + ORDER BY (both entirely over pdv for the indexed sorts — device is the
-- tiebreaker AND the PK) push into the 0106 expression indexes; visible_devices and the
-- display joins are probed per scanned row. Every device has a pdv row from creation
-- (DeviceRegistry.CreateDeviceAsync) + the engine's per-pass backfill, so driving from
-- pdv never hides a device. The obscured-MAC fingerprint is the one remaining lateral —
-- deliberately not materialized (registry-only display fallback, no sort).
WITH device_sources AS (
    SELECT device_id, source FROM device_discovery_sources
)
SELECT
    d.device_id
    -- The real, agent-reported OS hostname only — null for a passively-discovered
    -- device with no agent. Never backfilled from friendly-name-ish sources.
  , s.hostname
    -- Context-derivation finals (identity-* — see ContextDerivationLibrary): the
    -- resolved display rollup, best identity IP, and newest MAC, recomputed
    -- set-based on ingest instead of per-row laterals here.
  , pdv.friendly_name
  , pdv.ip
  , pdv.mac
  , lower(dmac_obs.fp_value) AS obscured_mac
  , pdv.vendor
    -- When the real MAC never reconstructs (ObscuredMac.Pick found no unique
    -- candidate), the obscured value still carries a trustworthy OUI in its first
    -- 6 hex digits (see ObscuredMac.cs) — fall back so vendor still resolves.
  , COALESCE(
        oui_vendor(pdv.mac),
        oui_vendor(left(regexp_replace(lower(dmac_obs.fp_value), '[^0-9a-f]', '', 'g'), 6))
    ) AS oui
  , COALESCE(
        oui_country(pdv.mac),
        oui_country(left(regexp_replace(lower(dmac_obs.fp_value), '[^0-9a-f]', '', 'g'), 6))
    ) AS oui_country
  , s.os_family
  , s.os_distro
  , d.management_status
  , src_agg.sources
  , d.last_seen
    -- COALESCE keeps the cursor value non-null and, as an expression, gives sort_key the same
    -- (nullable) reported schema in every sort variant; the keyset comparison and ORDER BY below
    -- use the raw sort expression so the expression indexes still match.
  , COALESCE(__SORT_KEY__, '') AS sort_key
FROM
    proj_devices pdv
    JOIN visible_devices d
    ON d.device_id::text = pdv.device
    LEFT JOIN proj_systems s
    ON s.device = pdv.device
    LEFT JOIN LATERAL (
        SELECT fp_value FROM device_fingerprints
        WHERE device_id = d.device_id AND fp_type = 'obscured-mac'
        ORDER BY last_seen DESC LIMIT 1) dmac_obs ON TRUE
    LEFT JOIN LATERAL (
        SELECT string_agg(DISTINCT ds.source, ',' ORDER BY ds.source) AS sources
        FROM device_sources ds
        WHERE ds.device_id = d.device_id) src_agg ON TRUE
WHERE
      (
          $1::text IS NULL
     OR d.management_status = $1
          )
  AND (
          $2::text IS NULL
     OR EXISTS (
            SELECT 1 FROM device_sources ds WHERE ds.device_id = d.device_id AND ds.source = $2)
          )
  AND (
          $3::text IS NULL
     OR s.os_family = $3
          )
  AND (
          $4::text IS NULL
     OR pdv.vendor = $4
          )
  AND (
          $5::text IS NULL
     OR coalesce(pdv.friendly_name, '') ILIKE '%' || $5 || '%'
     OR EXISTS (
            SELECT 1 FROM device_fingerprints qf
            WHERE qf.device_id = d.device_id AND qf.fp_value ILIKE '%' || $5 || '%')
          )
  AND (
          $6::text IS NULL
     OR (__SORT_KEY__, pdv.device) __CMP__ ($6, $7)
          )
ORDER BY
    __SORT_KEY__ __DIR__
  , pdv.device __DIR__
LIMIT $8
