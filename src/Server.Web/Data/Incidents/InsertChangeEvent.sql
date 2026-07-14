INSERT INTO change_events (event_type, entity_kind, entity_id, detail, occurred_at)
VALUES ($1, $2, $3, $4, now())
RETURNING id
