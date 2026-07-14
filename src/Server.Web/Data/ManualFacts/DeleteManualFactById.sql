-- Reverting a single device's manual override/custom value: only ever deletes rows this
-- feature itself wrote (source = ManualEntry), never a collector's history for the same id.
DELETE FROM facts_history
WHERE id = $1
  AND source = $2
RETURNING id
