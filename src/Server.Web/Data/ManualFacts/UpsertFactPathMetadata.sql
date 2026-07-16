-- Upserts path-level label/description metadata for an operator-authored fact, keyed by its
-- device-independent identity (attribute_path $1, non-device key_values $2 as canonical jsonb).
-- $3 label, $4 description, $5 acting user, $6 show-in-reports flag (NULL = leave unchanged;
-- callers that only touch label/description pass NULL). On conflict the label/description are
-- replaced and the updated_at/updated_by audit columns advanced (architecture §3.2, §5.2).
-- Returns the row id.
-- $2 is the fact's FULL canonical key_values (path order, incl. Device); the Device entry is
-- stripped here so the stored key is the device-independent identity.
-- NULLIF/COALESCE on $2: callers always pass canonical JSON, but the schema-validation harness
-- executes with '' — treat a blank as the empty key rather than failing the jsonb cast.
INSERT INTO fact_path_metadata (attribute_path, key_values, label, description, created_by, updated_by, show_in_reports)
VALUES ($1, (COALESCE(NULLIF($2, '')::jsonb, '{}'::jsonb) - 'Device'), $3, $4, $5, $5, COALESCE($6, FALSE))
ON CONFLICT (attribute_path, key_values) DO UPDATE
    SET label       = EXCLUDED.label
      -- Preserve an existing description when the caller passes NULL (e.g. the browse "Edit label"
      -- affordance sends label only); there is no UI path that needs to clear a description.
      , description  = COALESCE(EXCLUDED.description, fact_path_metadata.description)
      -- Same preserve-on-NULL rule: only an explicit true/false moves the flag.
      , show_in_reports = COALESCE($6, fact_path_metadata.show_in_reports)
      , updated_at   = now()
      , updated_by   = EXCLUDED.updated_by
RETURNING id
