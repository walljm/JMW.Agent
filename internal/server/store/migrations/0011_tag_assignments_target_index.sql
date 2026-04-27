-- Index for fast lookup of tags by (target_kind, target_id).
-- The base tables (tags, tag_assignments) were created in 0001_initial.sql.
CREATE INDEX IF NOT EXISTS idx_tag_assignments_target
    ON tag_assignments(target_kind, target_id);
