-- Add seen_count to hostname_aliases so the UI can show how many times
-- each name was reported rather than always displaying 1.
ALTER TABLE hostname_aliases ADD COLUMN seen_count INTEGER NOT NULL DEFAULT 1;
