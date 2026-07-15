-- Opens a new incident, reopens a recently-resolved one (flap suppression), or just refreshes
-- last_seen_at/detail on an already-open one — all in one round trip.
-- $1 entity_kind, $2 entity_id, $3 incident_type, $4 detail, $5 reopen window (seconds)
WITH reopened AS (
    UPDATE incidents i
    SET resolved_at = NULL, resolution = NULL, last_seen_at = now(), detail = $4
    WHERE i.id = (
        SELECT id
        FROM incidents
        WHERE entity_kind = $1 AND entity_id = $2 AND incident_type = $3
          AND resolved_at IS NOT NULL
        ORDER BY resolved_at DESC
        LIMIT 1
    )
    AND i.resolved_at IS NOT NULL
    AND i.resolved_at >= now() - make_interval(secs => $5)
    RETURNING i.id AS id
)
INSERT INTO incidents (incident_type, entity_kind, entity_id, detail, opened_at, last_seen_at)
SELECT $3, $1, $2, $4, now(), now()
WHERE NOT EXISTS (SELECT 1 FROM reopened)
ON CONFLICT (entity_kind, entity_id, incident_type) WHERE resolved_at IS NULL
    DO UPDATE SET last_seen_at = now(), detail = EXCLUDED.detail
RETURNING id AS id
