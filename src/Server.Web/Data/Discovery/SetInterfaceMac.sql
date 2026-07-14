-- Fill in the reconstructed full MAC for an interface row (keyed by device +
-- interface). The obscured_mac stays as the raw kept fact. Unlike the discovered
-- path this does not resolve or merge a device — it is the device's own interface.
UPDATE proj_interfaces
SET
    mac_address = $3
WHERE
      device = $1
  AND interface = $2
RETURNING
    device
