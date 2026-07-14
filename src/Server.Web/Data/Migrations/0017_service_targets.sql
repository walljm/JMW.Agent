-- Server-managed service collection targets, delivered to agents via the heartbeat
-- config block alongside device collection_targets. A service target points a
-- registered IServiceCollector (by service_type slug) at a remote API URL, with an
-- optional credential from the shared credentials table (api-token for Technitium /
-- Home Assistant). Enabled targets are merged with the agent's file-configured
-- services on each collection cycle.

CREATE TABLE if NOT EXISTS service_targets (
    service_target_id
    UUID
    NOT
    NULL
    DEFAULT
    gen_random_uuid(
                   ) PRIMARY KEY,
    agent_id UUID NOT NULL REFERENCES agents (agent_id
                                             ) ON DELETE CASCADE,
    service_type TEXT NOT NULL, -- technitium-dns | home-assistant | ...
    url TEXT NOT NULL,
    label TEXT, -- optional human label shown in agent logs
    credential_id UUID REFERENCES credentials (credential_id
                                              ),
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now (
                                                ),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now (
                                                )
    );
CREATE INDEX if NOT EXISTS service_targets_agent_idx ON service_targets (agent_id);
CREATE INDEX if NOT EXISTS service_targets_keyset_idx ON service_targets (created_at DESC, service_target_id);
