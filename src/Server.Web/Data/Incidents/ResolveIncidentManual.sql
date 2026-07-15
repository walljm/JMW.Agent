-- Manually resolves the open incident for this entity+type (admin action — e.g. a fingerprint
-- conflict merge/exclude), as opposed to ResolveIncident.sql's automatic value-crossed-back resolve.
UPDATE incidents
SET resolved_at = now(), resolution = 'manual', last_seen_at = now()
WHERE entity_kind = $1 AND entity_id = $2 AND incident_type = $3 AND resolved_at IS NULL
RETURNING id AS id
