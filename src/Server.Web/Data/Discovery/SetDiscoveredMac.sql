-- Fill in the reconstructed full MAC for a discovered row (keyed by observer +
-- neighbor IP). The obscured_mac stays as the raw kept fact.
UPDATE proj_discovered
SET
    mac = $3
WHERE
      device = $1
  AND discovered = $2 RETURNING
    device
