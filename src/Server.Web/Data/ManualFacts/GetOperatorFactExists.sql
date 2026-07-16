-- Whether an operator-authored (source $2 = FactSource.ManualEntry) value already exists at this
-- exact fact id ($1) — backs the overwrite-confirmation guard (REQ-005, architecture §5.2).
SELECT
    EXISTS(SELECT 1 FROM facts_history WHERE id = $1 AND source = $2) AS in_use
