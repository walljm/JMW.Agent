-- 0003 device inventory blob
PRAGMA foreign_keys = ON;

ALTER TABLE agents ADD COLUMN inventory_json TEXT;
ALTER TABLE agents ADD COLUMN inventory_collected_at TEXT;
