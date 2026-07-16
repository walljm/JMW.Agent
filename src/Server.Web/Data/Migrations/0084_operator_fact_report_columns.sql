-- Arbitrary operator facts as report columns (follow-up to 0083_operator_facts).
-- A device-scoped arbitrary fact path can be flagged to appear as an extra column in reports
-- that list devices (currently the Devices report). The flag lives on the path's existing
-- device-independent metadata row; values are read straight from the facts_history operator
-- subset (source = 2, fronted by facts_history_operator_path_idx) — no projection table needed.
ALTER TABLE fact_path_metadata
    ADD COLUMN IF NOT EXISTS show_in_reports BOOLEAN NOT NULL DEFAULT FALSE;
