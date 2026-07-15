-- Auto-resolves the open incident for this entity+type, if any (no-op if already resolved/absent).
UPDATE incidents
SET resolved_at = now(), resolution = 'auto', last_seen_at = now()
WHERE entity_kind = $1 AND entity_id = $2 AND incident_type = $3 AND resolved_at IS NULL
RETURNING id AS id
