SELECT
    c.device
  , s.hostname
  , c.container
  , c.name
  , c.image
  , c.state
  , c.health
  , c.cpu_pct
  , c.mem_usage_bytes
  , c.restart_count
  , c.compose_project
  , c.compose_service
  , c.restart_policy
    -- COALESCE keeps the cursor value non-null and, as an expression, gives sort_key the same
    -- (nullable) reported schema in every sort variant; the keyset comparison and ORDER BY below
    -- use the raw sort expression so the expression indexes still match.
  , COALESCE(__SORT_KEY__, '') AS sort_key
  , COALESCE(s.friendly_name, s.hostname) AS friendly_name
FROM
    proj_containers c
    LEFT JOIN proj_systems s
    ON s.device = c.device
WHERE
      (
          $1::text IS NULL
     OR c.state = $1
          )
  AND (
          $2::text IS NULL
     OR c.image LIKE '%' || $2 || '%'
          )
  AND (
          $3::text IS NULL
     OR (__SORT_KEY__, c.device, c.container) __CMP__ ($3, $4, $5)
          )
ORDER BY
    __SORT_KEY__ __DIR__
  , c.device __DIR__
  , c.container __DIR__
LIMIT $6
