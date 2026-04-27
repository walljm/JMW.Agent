-- 0004 denormalized primary IP for fast list rendering
PRAGMA foreign_keys = ON;

ALTER TABLE agents ADD COLUMN primary_ip TEXT;
