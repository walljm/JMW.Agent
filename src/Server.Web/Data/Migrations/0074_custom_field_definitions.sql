-- Operator-defined custom device fields (docs/plans/user-provided.md). Each row is a schema
-- definition ("Warranty Expiration", attached to the Hardware Details sheet); the per-device
-- VALUES are ordinary facts at Device[{id}].Custom[{slug}].Value (FactSource.ManualEntry),
-- rendered by CustomFieldViewMerger. Deleting a definition cascades to those facts_history rows
-- (see ManualFactQueries.DeleteManualFactsByCustomSlugAsync) — this table only holds the schema.
CREATE TABLE IF NOT EXISTS custom_field_definitions (
    id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    label TEXT NOT NULL,
    -- The Device[].Custom[slug].Value list key. Immutable once created (unlike label, which is
    -- just column-header display text) — renaming it would orphan the old slug's facts_history
    -- rows with no definition pointing at them. Kebab-case, validated at the API layer.
    slug TEXT NOT NULL UNIQUE,
    -- NULL = no specific target: the value renders in the baseline "Custom Fields" list view
    -- (FactViewLibrary.CustomFieldsViewTitle) by its raw slug. Non-null + is_new_view = false
    -- must match an existing Properties-kind FactViewDef title (validated at the API layer, not
    -- by a DB constraint — the set of valid titles lives in compiled FactViewLibrary.All).
    target_view_title TEXT,
    -- Only meaningful when is_new_view = true: the FactViewGroup (by enum name) the synthesized
    -- view files under in the device-detail section nav.
    target_view_group TEXT,
    is_new_view BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by TEXT NOT NULL
);
