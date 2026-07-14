-- Device targets (collection_targets) had no label field, unlike service_targets — an
-- inconsistency worth fixing on its own so operators can name a target the same way for
-- either kind (e.g. "core switch" instead of re-reading the raw IP every time).
ALTER TABLE collection_targets
    ADD COLUMN label text;
