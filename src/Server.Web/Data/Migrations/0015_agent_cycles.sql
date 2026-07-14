-- Agent cycle summaries: one row per heartbeat cycle, recording what ran,
-- how long it took, and how many facts/devices were collected.

CREATE TABLE if NOT EXISTS agent_cycles (
    cycle_id
    BIGSERIAL
    PRIMARY
    KEY,
    agent_id
    UUID
    NOT
    NULL
    REFERENCES
    agents(
    agent_id
          ) ON DELETE CASCADE,
    cycle_at TIMESTAMPTZ NOT NULL,
    duration_ms INT NOT NULL DEFAULT 0,
    facts_sent INT NOT NULL DEFAULT 0,
    error_count INT NOT NULL DEFAULT 0,
    collectors JSONB NOT NULL DEFAULT '[]',
    scanners JSONB NOT NULL DEFAULT '[]'
    );

CREATE INDEX if NOT EXISTS ix_agent_cycles_agent_id_cycle_at
    ON agent_cycles (agent_id, cycle_at DESC);

-- Retention: keep 7 days of cycle history (volatile, high-write).
INSERT INTO retention_policies
(
      table_name
    , category
    , time_column
    , stale_after
    , notes
)
VALUES
    (
          'agent_cycles'
        , 'volatile'
        , 'cycle_at'
        , INTERVAL '7 days'
        , 'Agent heartbeat cycle summaries; high write volume, short diagnostic window'
    ) ON CONFLICT (TABLE_NAME) DO NOTHING;
