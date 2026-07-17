-- All discovered rows carrying an obscured MAC (Google Wifi), whether or not the
-- full `mac` has been reconstructed yet. `discovered` is the neighbor IP.
-- The materializer reconstructs rows with a null `mac`, then promotes each row's
-- intrinsic attributes onto the resolved Device[] — decoupled from the
-- reconstruction instant so late-arriving enrichment (device_type, model) still
-- promotes on a later cycle.
-- device_type/cast_id/os read from materialization_facts (docs/plans/
-- architecture-identity-facts.md §5 Phase 2e); mac/obscured_mac/hostname/model/friendly_name/
-- vendor stay on proj_discovered (§2).
SELECT
    d.device
  , d.discovered AS ip
  , d.obscured_mac
  , d.mac
  , d.hostname
  , d.model
  , d.friendly_name
  , idf.device_type
  , idf.cast_id
  , d.vendor
  , idf.os
  , d.agent_id
FROM
    proj_discovered d
    LEFT JOIN (
        SELECT
            device
          , entity_key
          , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].CastId')     AS cast_id
          , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].DeviceType') AS device_type
          , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].Os')         AS os
        FROM
            materialization_facts
        WHERE
            attribute_path IN (
                'Device[].Discovered[].CastId', 'Device[].Discovered[].DeviceType',
                'Device[].Discovered[].Os'
            )
        GROUP BY
            device, entity_key
    ) idf ON idf.device = d.device AND idf.entity_key = d.discovered
WHERE
    d.obscured_mac IS NOT NULL
