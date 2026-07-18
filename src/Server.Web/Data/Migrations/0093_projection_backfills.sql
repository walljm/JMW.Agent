-- Watermark for the one-time facts_history -> projection backfill (ProjectionSchemaService).
-- A projection populates only from LIVE fact routing; there is no automatic replay of history.
-- Combined with agent delta-tracking (unchanged facts are never re-sent) and the router's silent
-- drop of facts that have no matching projection, this means a projection added AFTER its facts
-- already landed in facts_history stays empty forever -- exactly how proj_docker_networks shipped
-- empty in 0091 despite Device[].DockerNet[].* facts existing. This table lets the backfill run
-- exactly once per projection (the deploy that introduces it), then never again -- live routing
-- owns everything after that.
CREATE TABLE IF NOT EXISTS projection_backfills (
    table_name TEXT NOT NULL PRIMARY KEY,
    backfilled_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
