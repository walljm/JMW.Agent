SELECT
    container
  , name
  , image
  , state
  , health
  , restart_count
  , updated_at
FROM
    proj_containers
WHERE
    device = $1
ORDER BY
    name      ASC NULLS LAST
  , container ASC
