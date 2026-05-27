-- 0022 backfill data from legacy `devices` table into the entity model
-- before the legacy tables are dropped in 0023.
--
-- This preserves user-entered context (notes) and the auto-derived device
-- classification (kind). Network-observable fields like IP, hostname, and
-- vendor are regenerated organically from agent + terrain observations.

-- Add device_kind column to hardware. The pipeline's derive step populates
-- this from observation signals (mDNS services, vendor OUI, hostname patterns).
ALTER TABLE hardware ADD COLUMN device_kind TEXT;

-- Backfill notes onto matching interfaces (joined by MAC).
UPDATE interfaces
SET notes = (
  SELECT d.notes FROM devices d
  WHERE LOWER(d.mac) = LOWER(interfaces.mac)
    AND COALESCE(d.notes, '') != ''
  LIMIT 1
)
WHERE EXISTS (
  SELECT 1 FROM devices d
  WHERE LOWER(d.mac) = LOWER(interfaces.mac)
    AND COALESCE(d.notes, '') != ''
);

-- Backfill device_kind onto matching hardware (joined via interfaces.mac).
-- Prefer the most specific (non-"unknown") value if multiple interfaces map
-- to the same hardware.
UPDATE hardware
SET device_kind = (
  SELECT d.kind FROM devices d
  JOIN interfaces i ON LOWER(i.mac) = LOWER(d.mac)
  WHERE i.hardware_id = hardware.id
    AND d.kind IS NOT NULL
    AND d.kind != ''
    AND d.kind != 'unknown'
  LIMIT 1
)
WHERE EXISTS (
  SELECT 1 FROM devices d
  JOIN interfaces i ON LOWER(i.mac) = LOWER(d.mac)
  WHERE i.hardware_id = hardware.id
    AND d.kind IS NOT NULL
    AND d.kind != ''
    AND d.kind != 'unknown'
);
