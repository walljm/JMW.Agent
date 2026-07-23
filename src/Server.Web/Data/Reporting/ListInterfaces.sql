-- JOIN proj_devices is safe (never drops rows): every device has a proj_devices row
-- from creation (DeviceRegistry.CreateDeviceAsync + the context engine's backfill).
SELECT
    i.device
  , s.hostname
  , i.name
  , i.mac_address
  , i.obscured_mac
    -- Obscured MACs (Google Wifi/OnHub firmware) preserve only the real OUI (first 6
    -- hex nibbles); the rest is fabricated. When there's no reconstructed real MAC,
    -- fall back to just that trustworthy prefix (same idiom as ListDeviceReport.sql).
  , COALESCE(
        oui_vendor(i.mac_address),
        oui_vendor(left(regexp_replace(lower(i.obscured_mac), '[^0-9a-f]', '', 'g'), 6))
    ) AS oui
  , COALESCE(
        oui_country(i.mac_address),
        oui_country(left(regexp_replace(lower(i.obscured_mac), '[^0-9a-f]', '', 'g'), 6))
    ) AS oui_country
  , i.ipv4
  , i.ipv6
  , i.mtu
  , i.up
  , i.loopback
  , i.speed_bps
  , i.duplex
  , i.type
  , i.interface
    -- COALESCE keeps the cursor value non-null and, as an expression, gives sort_key the same
    -- (nullable) reported schema in every sort variant; the keyset comparison and ORDER BY below
    -- use the raw sort expression so the expression indexes still match.
  , COALESCE(__SORT_KEY__, '') AS sort_key
  , COALESCE(s.friendly_name, s.hostname) AS friendly_name
FROM
    proj_interfaces i
    JOIN proj_devices pdv
    ON pdv.device = i.device
    LEFT JOIN proj_systems s
    ON s.device = i.device
WHERE
      (
          $1::text IS NULL
     OR COALESCE(s.hostname, '') ILIKE '%' || $1 || '%'
     OR COALESCE(i.name, '') ILIKE '%' || $1 || '%'
     OR COALESCE(i.ipv4, '') ILIKE '%' || $1 || '%'
          )
  AND (
          $2::text IS NULL
          -- Decomposed keyset cursor (context-derivations.md §3.3): the exact 3-column row
          -- comparison spans two tables for the cross-table hostname sort, so it can't push into
          -- proj_devices' index by itself — the redundant inclusive prefix bound (implied by the
          -- exact comparison, so it never changes results) restores the Index Cond while the
          -- exact residual keeps page boundaries precise. Same-table sorts push their full tuple
          -- into their expression index and simply carry the implied prefix as an extra filter.
     OR (
            ((__SORT_KEY__, pdv.device) __CMP__= ($2, $3))
        AND ((__SORT_KEY__, i.device, i.interface) __CMP__ ($2, $3, $4))
            )
          )
ORDER BY
    __SORT_KEY__ __DIR__
  , i.device __DIR__
  , i.interface __DIR__
LIMIT $5
