-- Incident/event change model (docs: "From Noise to Signal" design proposal). Replaces the raw
-- fact-diff Change Feed with two curated streams, tagged once per incident/event type rather than
-- guessed at query time:
--   incidents      — has a lifecycle (opens, later resolves); at most one OPEN row per
--                    entity+type, enforced by incidents_open_uq below.
--   change_events  — one-shot, timestamped, no duration (discovered/promoted/merged/...).
-- Both are fed by IncidentEvaluator (Ingest/Incidents/), hooked into FactIngestPipeline
-- alongside AppendAsync/RouteAsync, plus a few non-fact-driven call sites (device
-- promotion/merge, fingerprint conflicts, the agent-liveness sweep) — see IncidentTypeRegistry.

CREATE TABLE IF NOT EXISTS incidents (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    incident_type TEXT NOT NULL, -- 'smart_failing' | 'filesystem_full' | 'container_not_running' |
                                  -- 'service_down' | 'fingerprint_conflict' | 'agent_offline'
    entity_kind TEXT NOT NULL,   -- 'device' | 'service' | 'agent' | 'fingerprint'
    entity_id TEXT NOT NULL,     -- device/service/agent UUID as text, or "{fp_type}:{fp_value}"
                                  -- for fingerprint_conflict (no single device owns the conflict)
    detail TEXT,
    opened_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    resolved_at TIMESTAMPTZ,     -- NULL = currently open
    resolution TEXT,             -- 'auto' | 'manual' — set when resolved_at is set
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- At most one OPEN incident per entity+type — enforced by Postgres, not app code. The
-- upsert in IncidentEvaluator relies on this to detect "already open" vs "needs a new row".
CREATE UNIQUE INDEX IF NOT EXISTS incidents_open_uq
    ON incidents (entity_kind, entity_id, incident_type)
    WHERE resolved_at IS NULL;

-- Reopen-window lookup: "most recent incident of this entity+type, resolved or not" — used to
-- decide whether a recurrence continues the existing row (flap suppression) or starts a new one.
CREATE INDEX IF NOT EXISTS incidents_entity_type_recent_idx
    ON incidents (entity_kind, entity_id, incident_type, resolved_at DESC NULLS FIRST, opened_at DESC);

-- Dashboard "Needs Attention": open incidents grouped by type, fleet-wide.
CREATE INDEX IF NOT EXISTS incidents_open_type_idx
    ON incidents (incident_type)
    WHERE resolved_at IS NULL;

-- Recent Activity (fleet-wide) and Device Detail History (per-entity) both order by recency.
CREATE INDEX IF NOT EXISTS incidents_opened_at_idx ON incidents (opened_at DESC);
CREATE INDEX IF NOT EXISTS incidents_entity_idx ON incidents (entity_kind, entity_id, opened_at DESC);

CREATE TABLE IF NOT EXISTS change_events (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_type TEXT NOT NULL, -- 'discovered' | 'promoted' | 'merged'
    entity_kind TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    detail TEXT,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS change_events_occurred_at_idx ON change_events (occurred_at DESC);
CREATE INDEX IF NOT EXISTS change_events_entity_idx ON change_events (entity_kind, entity_id, occurred_at DESC);

-- change_events is one-shot history, safe to prune like facts_history — an event already
-- happened, so pruning old rows loses no live state. incidents deliberately has NO retention
-- policy registered here: an open incident's last_seen_at can go stale if the entity stops
-- reporting entirely (device unplugged mid-SMART-failure), and pruning it would silently drop an
-- unresolved incident and free its slot in incidents_open_uq. Revisit once there's a policy for
-- "resolved incidents older than N days" that only ever touches resolved_at IS NOT NULL rows.
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
          'change_events'
        , 'history'
        , 'occurred_at'
        , INTERVAL '90 days'
        , 'One-shot event log; prune on occurred_at, matches facts_history retention.'
    ) ON CONFLICT (TABLE_NAME) DO NOTHING;
