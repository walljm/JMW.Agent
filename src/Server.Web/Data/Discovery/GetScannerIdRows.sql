-- Rows carrying a vendor identity id (Hue bridge id / ONVIF hardware id). Each id promotes to
-- a fingerprint; the row's MAC (when known) is unioned in the resolve so the id merges onto the
-- same device an ARP/scanner observer sees by MAC.
-- Identity ids read from materialization_facts (docs/plans/architecture-identity-facts.md §5
-- Phase 2b); mac stays on proj_discovered (§2 — reports join on it directly).
SELECT
    d.mac
  , idf.hue_bridge_id
  , idf.onvif_hardware_id
FROM (
    SELECT
        device
      , entity_key
      , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].HueBridgeId') AS hue_bridge_id
      , MAX(value) FILTER (WHERE attribute_path = 'Device[].Discovered[].OnvifHardwareId') AS onvif_hardware_id
    FROM
        materialization_facts
    WHERE
        attribute_path IN ('Device[].Discovered[].HueBridgeId', 'Device[].Discovered[].OnvifHardwareId')
    GROUP BY
        device, entity_key
) idf
JOIN proj_discovered d ON d.device = idf.device AND d.discovered = idf.entity_key
