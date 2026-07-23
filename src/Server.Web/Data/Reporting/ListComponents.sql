-- JOIN proj_devices is safe (never drops rows): every device has a proj_devices row
-- from creation (DeviceRegistry.CreateDeviceAsync + the context engine's backfill).
SELECT
    c.device
  , s.hostname
  , c.hwcomponent
  , c.class
  , c.slot
  , c.description
  , c.vendor
  , c.model
  , c.serial
  , c.firmware
  , c.status
  , c.is_fru
    -- COALESCE keeps the cursor value non-null and, as an expression, gives sort_key the same
    -- (nullable) reported schema in every sort variant; the keyset comparison and ORDER BY below
    -- use the raw sort expression so the expression indexes still match.
  , COALESCE(__SORT_KEY__, '') AS sort_key
  , COALESCE(s.friendly_name, s.hostname) AS friendly_name
FROM
    proj_hardware_inventory c
    JOIN proj_devices pdv
    ON pdv.device = c.device
    LEFT JOIN proj_systems s
    ON s.device = c.device
WHERE
      (
          $1::text IS NULL
     OR COALESCE(s.hostname, '') ILIKE '%' || $1 || '%'
     OR COALESCE(c.description, '') ILIKE '%' || $1 || '%'
     OR COALESCE(c.vendor, '') ILIKE '%' || $1 || '%'
     OR COALESCE(c.model, '') ILIKE '%' || $1 || '%'
          )
  AND (
          $2::text IS NULL
     OR c.class = $2
          )
  AND (
          $3::text IS NULL
          -- Decomposed keyset cursor (context-derivations.md §3.3): the exact 3-column row
          -- comparison spans two tables for the cross-table hostname sort, so it can't push into
          -- proj_devices' index by itself — the redundant inclusive prefix bound (implied by the
          -- exact comparison, so it never changes results) restores the Index Cond while the
          -- exact residual keeps page boundaries precise. Same-table sorts push their full tuple
          -- into the 0104 index and simply carry the implied prefix as an extra filter.
     OR (
            ((__SORT_KEY__, pdv.device) __CMP__= ($3, $4))
        AND ((__SORT_KEY__, c.device, c.hwcomponent) __CMP__ ($3, $4, $5))
            )
          )
ORDER BY
    __SORT_KEY__ __DIR__
  , c.device __DIR__
  , c.hwcomponent __DIR__
LIMIT $6
