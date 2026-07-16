-- All discovered rows carrying an obscured MAC (Google Wifi), whether or not the
-- full `mac` has been reconstructed yet. `discovered` is the neighbor IP.
-- The materializer reconstructs rows with a null `mac`, then promotes each row's
-- intrinsic attributes onto the resolved Device[] — decoupled from the
-- reconstruction instant so late-arriving enrichment (device_type, model) still
-- promotes on a later cycle.
SELECT
    device
  , discovered AS ip
  , obscured_mac
  , mac
  , hostname
  , model
  , friendly_name
  , device_type
  , cast_id
  , vendor
  , os
FROM
    proj_discovered
WHERE
    obscured_mac IS NOT NULL
