SET
search_path TO jmwdiscovery, PUBLIC;

-- Schema alignment backfill for objects/columns present in canonical Schema.sql
-- but missing in some deployed databases.

CREATE TABLE if NOT EXISTS bootstrap_token (
    token_hash
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    created_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    used_at TIMESTAMPTZ
    );

ALTER TABLE proj_systems
    ADD COLUMN if NOT EXISTS last_seen_ip TEXT;

ALTER TABLE proj_interfaces
    ADD COLUMN if NOT EXISTS alias TEXT,
    ADD COLUMN IF NOT EXISTS admin_status TEXT,
    ADD COLUMN IF NOT EXISTS oper_status TEXT,
    ADD COLUMN IF NOT EXISTS ipv4 TEXT,
    ADD COLUMN IF NOT EXISTS ipv6 TEXT;

ALTER TABLE proj_docker
    ADD COLUMN if NOT EXISTS os TEXT,
    ADD COLUMN IF NOT EXISTS kernel TEXT,
    ADD COLUMN IF NOT EXISTS cpu_count INTEGER,
    ADD COLUMN IF NOT EXISTS mem_bytes BIGINT;

ALTER TABLE proj_disks
    ADD COLUMN if NOT EXISTS smart_available_spare_pct DOUBLE PRECISION,
    ADD COLUMN IF NOT EXISTS smart_pct_used DOUBLE PRECISION,
    ADD COLUMN IF NOT EXISTS smart_pending_sectors BIGINT,
    ADD COLUMN IF NOT EXISTS smart_data_read_gb DOUBLE PRECISION,
    ADD COLUMN IF NOT EXISTS smart_data_written_gb DOUBLE PRECISION;
