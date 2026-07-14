SELECT
    fp_type
  , fp_value
  , source
  , last_seen
FROM
    device_fingerprints
WHERE
    device_id = $1
ORDER BY
    source   ASC NULLS LAST
  , fp_type  ASC
  , fp_value ASC
