-- Same predicate as ConflictsApi's admin listing: fingerprint pairs shared by more than one
-- device, excluding ones an admin has already excluded. Used by FingerprintConflictSweepService
-- to open/keep-open a fingerprint_conflict incident per conflicting pair.
SELECT
    df.fp_type
  , df.fp_value
FROM
    device_fingerprints df
WHERE
    NOT EXISTS (
        SELECT 1
        FROM excluded_fingerprints ef
        WHERE ef.fp_type = df.fp_type AND ef.fp_value = df.fp_value
    )
GROUP BY
    df.fp_type
  , df.fp_value
HAVING
    count(DISTINCT df.device_id) > 1
