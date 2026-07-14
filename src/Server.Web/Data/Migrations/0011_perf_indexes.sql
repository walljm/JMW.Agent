-- PERF-007: trigram indexes for ILIKE hostname and fingerprint-value searches
CREATE
EXTENSION IF NOT EXISTS pg_trgm;

CREATE INDEX if NOT EXISTS ix_proj_systems_hostname_trgm
    ON proj_systems USING GIN (hostname gin_trgm_ops);

CREATE INDEX if NOT EXISTS ix_device_fingerprints_fpvalue_trgm
    ON device_fingerprints USING GIN (fp_value gin_trgm_ops);

-- PERF-006: expression index on key_values->>'Device' used by ListChanges JOIN
CREATE INDEX if NOT EXISTS ix_facts_history_kv_device
    ON facts_history ((key_values->>'Device'));
