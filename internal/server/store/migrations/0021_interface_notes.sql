-- 0021 add notes column to interfaces for user annotations (replaces devices.notes)
ALTER TABLE interfaces ADD COLUMN notes TEXT NOT NULL DEFAULT '';
