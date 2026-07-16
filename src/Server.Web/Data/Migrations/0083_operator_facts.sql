-- Unified operator-authored device facts (docs/plans/architecture-operator-facts.md §3.2, §8).
-- Replaces two mechanisms — Manual Fact Overrides and Custom Fields — with one. Fact VALUES need
-- no migration: overrides and arbitrary facts are already ordinary FactSource.ManualEntry (source=2)
-- rows in facts_history. The only thing that needs a new home is Custom Fields' label metadata,
-- since an arbitrary fact is otherwise self-describing only by its path string.
--
-- This migration is transactional and must run BEFORE the code that removes the Custom Fields
-- subsystem — once custom_field_definitions is dropped its labels are unrecoverable from
-- facts_history (requirements §11).

-- 1. Path-level label/description metadata, keyed by the fact's device-INDEPENDENT identity:
--    attribute_path plus the non-device list keys (the Device entry is stripped). For a custom
--    field that is ('Device[].Custom[].Value', {"Custom":"<slug>"}) — one row shared across every
--    device. For a device-scoped arbitrary fact it is (path, {}).
CREATE TABLE IF NOT EXISTS fact_path_metadata (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    attribute_path TEXT  NOT NULL,
    key_values     JSONB NOT NULL DEFAULT '{}',
    label          TEXT,
    description    TEXT,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by     TEXT NOT NULL,
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by     TEXT,
    -- key_values is canonicalized (path order, by Fact.ComputeKeyValuesJson) before write, and
    -- Postgres compares jsonb by normalized value, so equivalent keys never collide.
    UNIQUE (attribute_path, key_values)
);

-- 2. Partial index serving the fleet-wide operator-facts browse (REQ-006): a cross-device scan over
--    the operator-authored (source=2) subset by attribute_path, which the existing
--    (id, collected_at DESC) index cannot front. source=2 is FactSource.ManualEntry.
CREATE INDEX IF NOT EXISTS facts_history_operator_path_idx
    ON facts_history (attribute_path, id)
    WHERE source = 2;

-- 3. Carry Custom Fields' label metadata forward. Each definition becomes one path-metadata row for
--    the shared Custom[].Value path, keyed by its slug. description is new (starts NULL); there was
--    no description or type column on custom_field_definitions to migrate.
INSERT INTO fact_path_metadata (attribute_path, key_values, label, created_at, created_by)
SELECT
    'Device[].Custom[].Value',
    jsonb_build_object('Custom', slug),
    label,
    created_at,
    created_by
FROM custom_field_definitions
ON CONFLICT (attribute_path, key_values) DO NOTHING;

-- 4. Retire the Custom Fields schema. Per-device values stay in facts_history untouched and now
--    classify as arbitrary facts (which is exactly what they are).
DROP TABLE custom_field_definitions;
