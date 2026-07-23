SELECT
    p.device
  , s.hostname
  , p.listeningport
  , p.protocol
  , p.address
  , p.port
  , p.process_name
  , p.pid
    -- COALESCE keeps the cursor value non-null and, as an expression, gives sort_key the same
    -- (nullable) reported schema in every sort variant; the keyset comparison and ORDER BY below
    -- use the raw sort expression so the expression indexes still match.
  , COALESCE(__SORT_KEY__, '') AS sort_key
  , COALESCE(s.friendly_name, s.hostname) AS friendly_name
FROM
    proj_ports p
    LEFT JOIN proj_systems s
    ON s.device = p.device
WHERE
      (
          $1::integer IS NULL
     OR p.port = $1
          )
  AND (
          $2::text IS NULL
     OR p.protocol = $2
          )
  AND (
          $3::text IS NULL
     OR (__SORT_KEY__, p.device, p.listeningport) __CMP__ ($3, $4, $5)
          )
ORDER BY
    __SORT_KEY__ __DIR__
  , p.device __DIR__
  , p.listeningport __DIR__
LIMIT $6
