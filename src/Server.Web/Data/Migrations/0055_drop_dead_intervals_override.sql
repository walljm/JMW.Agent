-- collection_targets.intervals_override (JSONB) was defined in 0001_initial_schema.sql but
-- AgentConfigAssembler.cs never read it — per-target interval overrides were apparently
-- planned and then dropped mid-build. No code in the repo references this column. Dropping
-- it rather than wiring it through: nothing currently depends on per-target override behavior,
-- and inventing that UI/assembler support here would be new scope beyond resolving the
-- dead column.
ALTER TABLE collection_targets
    DROP COLUMN intervals_override;
