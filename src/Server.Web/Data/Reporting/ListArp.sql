SELECT
    a.device
  , s.hostname           AS observer_hostname
  , a.arp                AS ip
  , a.mac
  , a.iface
  , a.state
  , CASE WHEN df.device_id IS NULL THEN NULL ELSE d.device_id END AS resolved_device_id
  , rs.hostname          AS resolved_hostname
  , oui_vendor(a.mac)    AS oui
  , oui_country(a.mac)   AS oui_country
    -- COALESCE keeps the cursor value non-null and, as an expression, gives sort_key the same
    -- (nullable) reported schema in every sort variant; the keyset comparison and ORDER BY below
    -- use the raw sort expression so the expression indexes still match.
  , COALESCE(__SORT_KEY__, '') AS sort_key
  , COALESCE(rs.friendly_name, rs.hostname) AS resolved_friendly_name
FROM
    proj_device_arp a
    LEFT JOIN proj_systems s
    ON s.device = a.device
    LEFT JOIN device_fingerprints df
    ON df.fp_type = 'mac' AND df.fp_value = a.mac
    LEFT JOIN devices d
    ON d.device_id = df.device_id
    LEFT JOIN proj_systems rs
    ON rs.device = d.device_id::text
WHERE
      (
          $1::text IS NULL
     OR a.mac ILIKE '%' || $1 || '%'
     OR a.arp ILIKE '%' || $1 || '%'
          )
  AND (
          $2::text IS NULL
     OR (__SORT_KEY__, a.device, a.arp) __CMP__ ($2, $3, $4)
          )
ORDER BY
    __SORT_KEY__ __DIR__
  , a.device __DIR__
  , a.arp __DIR__
LIMIT $5
