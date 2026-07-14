-- metrics_raw: same column shape as facts_history, but range-partitioned by day
-- instead of dedup'd on write. Monotonic counters (interface Rx/Tx bytes/packets, and
-- the same-shaped derived/discovered-link counters — see FactPaths.MetricPaths) differ
-- on nearly every poll by construction, so facts_history's dedup-on-write (a LATERAL
-- LIMIT 1 lookup per fact id) is pure write amplification for them, not an optimization.
--
-- Retention here is DROP TABLE per day partition (metadata-only, near-instant) instead
-- of DELETE + vacuum — see MetricPartitionService, which provisions future partitions
-- and drops expired ones. This migration only seeds the partitions needed for writes to
-- land immediately after deploy; the service takes over from there.
--
-- Deliberately no path/key GIN indexes like facts_history has — those exist there for
-- ad-hoc time-series queries, which is explicitly out of scope here (nothing reads
-- metrics_raw as a time series; see docs/plans/metrics-retention.md). Adding them would tax
-- every write with index maintenance to serve a query pattern that doesn't exist yet.
CREATE TABLE IF NOT EXISTS metrics_raw
(
      id             TEXT             NOT NULL
    , attribute_path  TEXT             NOT NULL
    , key_values      JSONB            NOT NULL
    , kind            SMALLINT         NOT NULL
    , value_str       TEXT
    , value_long      BIGINT
    , value_double    DOUBLE PRECISION
    , collected_at    TIMESTAMPTZ      NOT NULL
    , source          SMALLINT         NOT NULL
    , source_name     TEXT             NOT NULL
    , PRIMARY KEY (id, collected_at)
) PARTITION BY RANGE (collected_at);

-- Seed today's and tomorrow's partitions so writes have somewhere to land immediately.
-- MetricPartitionService maintains a rolling 1-2 day lookahead (and drops expired
-- partitions) from here on — see docs/plans/metrics-retention.md §2.3.
DO $$
DECLARE
    partition_day date;
BEGIN
    FOR partition_day IN
        SELECT generate_series(CURRENT_DATE, CURRENT_DATE + 1, INTERVAL '1 day')::date
    LOOP
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS %I PARTITION OF metrics_raw FOR VALUES FROM (%L) TO (%L)',
            'metrics_raw_' || to_char(partition_day, 'YYYY_MM_DD'),
            partition_day,
            partition_day + 1
        );
    END LOOP;
END $$;
