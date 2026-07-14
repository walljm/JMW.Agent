-- Records which collector/scanner produced each fact (JMW.Discovery.Core.FactSource).
-- Two columns, kept in sync by the writer (FactRepository), not by a DB-side FK/lookup:
--   source      smallint -- the enum ordinal; cheap, indexable, used for filtering/joins
--   source_name text     -- the enum member name; for ad-hoc queries without memorizing
--                           the ordinal mapping
-- NOT NULL DEFAULT so existing rows and any writer that hasn't been updated yet stay
-- valid. The FactSource enum's numeric values are a permanent on-disk contract — see
-- the enum's doc comment before changing them.

ALTER TABLE facts_history
    ADD COLUMN source smallint NOT NULL DEFAULT 0,
    ADD COLUMN source_name text NOT NULL DEFAULT 'Unknown';
