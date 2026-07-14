SELECT entity_id
FROM incidents
WHERE entity_kind = $1 AND incident_type = $2 AND resolved_at IS NULL
