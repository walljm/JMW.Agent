-- 0008: containers reported by host agents.
--
-- Containers are first-class entities (list view, detail view, history),
-- but they are NOT agents: they have no PSK, no registration, no heartbeat.
-- Their lifecycle follows the host agent that reports them.
--
-- PK is (agent_id, container_id) so the same container ID across two hosts
-- (improbable but possible — IDs are SHA-256 prefixes) never collides.
--
-- Denormalized columns drive list/filter views without parsing the JSON
-- blob per row. The full report is in `record_json` for the detail page.
CREATE TABLE containers (
    agent_id        TEXT NOT NULL REFERENCES agents(id) ON DELETE CASCADE,
    container_id    TEXT NOT NULL,
    name            TEXT NOT NULL DEFAULT '',
    image           TEXT NOT NULL DEFAULT '',
    image_id        TEXT NOT NULL DEFAULT '',
    state           TEXT NOT NULL DEFAULT '',  -- running|exited|paused|...
    status          TEXT NOT NULL DEFAULT '',  -- human "Up 3 hours"
    health          TEXT NOT NULL DEFAULT '',  -- healthy|unhealthy|starting|none
    compose_project TEXT NOT NULL DEFAULT '',
    compose_service TEXT NOT NULL DEFAULT '',
    created_at      TEXT NOT NULL DEFAULT '',  -- container creation
    started_at      TEXT NOT NULL DEFAULT '',
    finished_at     TEXT NOT NULL DEFAULT '',
    first_seen_at   TEXT NOT NULL,             -- when WE first observed it
    last_seen_at    TEXT NOT NULL,             -- when WE last observed it
    record_json     TEXT NOT NULL DEFAULT '',  -- full proto.DockerContainer
    PRIMARY KEY (agent_id, container_id)
);
CREATE INDEX IF NOT EXISTS idx_containers_state ON containers(state);
CREATE INDEX IF NOT EXISTS idx_containers_compose ON containers(compose_project, compose_service);
CREATE INDEX IF NOT EXISTS idx_containers_last_seen ON containers(last_seen_at DESC);

-- Engine info per host (one row per agent that has Docker reachable).
CREATE TABLE docker_engines (
    agent_id        TEXT NOT NULL PRIMARY KEY REFERENCES agents(id) ON DELETE CASCADE,
    reachable       INTEGER NOT NULL DEFAULT 0,
    version         TEXT NOT NULL DEFAULT '',
    api_version     TEXT NOT NULL DEFAULT '',
    engine_id       TEXT NOT NULL DEFAULT '',
    os_type         TEXT NOT NULL DEFAULT '',
    architecture    TEXT NOT NULL DEFAULT '',
    storage_driver  TEXT NOT NULL DEFAULT '',
    cgroup_driver   TEXT NOT NULL DEFAULT '',
    swarm_state     TEXT NOT NULL DEFAULT '',
    record_json     TEXT NOT NULL DEFAULT '',  -- full proto.DockerEngine
    updated_at      TEXT NOT NULL
);
