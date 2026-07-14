---
agent: sdev-architecture
iteration: 2
date: 2026-06-04
status: draft
---

# Schema Additions for Iteration 2

Changes to apply to `scratch/Schema.sql`. All additive and idempotent (`IF NOT EXISTS` / `ADD COLUMN ... DEFAULT`) so existing data survives (brownfield — see each ENTITY's Migration Strategy). DDL shown for clarity; the implementation applies it via the existing schema file.

## 1. `devices` — add columns (ENTITY-001)

```sql
ALTER TABLE devices
    ADD COLUMN IF NOT EXISTS management_status TEXT        NOT NULL DEFAULT 'managed',
    ADD COLUMN IF NOT EXISTS merged_from        UUID[]      NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS updated_at          TIMESTAMPTZ NOT NULL DEFAULT now();

-- one-time backfill
UPDATE devices SET updated_at = created_at WHERE updated_at > created_at;

-- managed/discovered filter for the device list (keyset on last activity)
CREATE INDEX IF NOT EXISTS devices_status_idx ON devices (management_status);
```
`management_status` values: `managed` | `discovered` (REQ-030). Backfill defaults existing devices to `managed`.

## 2. `device_fingerprints` — add columns (ENTITY-002)

```sql
ALTER TABLE device_fingerprints
    ADD COLUMN IF NOT EXISTS last_seen TIMESTAMPTZ NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS source    TEXT;

UPDATE device_fingerprints SET last_seen = first_seen WHERE last_seen > first_seen;
```
`source` = reporting collector/agent (REQ-038), e.g. `ssh`, `snmp`, `passive:mdns`. NULL for pre-iter-2 rows.

## 3. `device_aliases` — new (ADR-002, REQ-052/053)

```sql
CREATE TABLE IF NOT EXISTS device_aliases (
    alias_device_id    UUID        NOT NULL PRIMARY KEY,
    survivor_device_id UUID        NOT NULL REFERENCES devices(device_id),
    merged_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS device_aliases_survivor_idx ON device_aliases (survivor_device_id);
```
A merged-away device_id resolves forward to its survivor. Lookups by device_id check `device_aliases` first.

## 4. `agents` — new (ENTITY-003, REQ-003/004)

```sql
CREATE TABLE IF NOT EXISTS agents (
    agent_id               UUID        NOT NULL PRIMARY KEY,
    hostname               TEXT        NOT NULL,
    status                 TEXT        NOT NULL DEFAULT 'pending',  -- pending|approved|disabled
    api_key_hash           TEXT        NOT NULL,
    last_heartbeat         TIMESTAMPTZ,
    zone                   TEXT,
    version                TEXT,
    passive_discovery_mode TEXT,                                    -- full|degraded
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS agents_api_key_hash_idx ON agents (api_key_hash);
CREATE INDEX IF NOT EXISTS agents_keyset_idx ON agents (created_at DESC, agent_id);
```

## 5. `collection_targets` — new (ENTITY-004, REQ-006/008/009)

**Superseded**: a later pass added a second, parallel `service_targets` table for service-style
polling (technitium-dns, home-assistant), then merged both into one `targets` table
(`endpoint`/`collector_type` columns, replacing `address`/`protocol`) once it was clear the
device/service split was accidental, not conceptual — see ENTITY-004 for the current shape.
`intervals_override` was also dropped early on (dead column, never wired to the config
assembler). DDL below is the original iteration-2 shape, kept for history.

```sql
CREATE TABLE IF NOT EXISTS collection_targets (
    target_id          UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    agent_id           UUID        NOT NULL REFERENCES agents(agent_id) ON DELETE CASCADE,
    address            TEXT        NOT NULL,
    protocol           TEXT        NOT NULL,            -- ssh|snmp|http|cert|...
    credential_id      UUID        REFERENCES credentials(credential_id),
    enabled            BOOLEAN     NOT NULL DEFAULT true,
    intervals_override JSONB,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS collection_targets_agent_idx  ON collection_targets (agent_id);
CREATE INDEX IF NOT EXISTS collection_targets_keyset_idx ON collection_targets (created_at DESC, target_id);
```

## 6. `credentials` — new (ENTITY-005, REQ-007, DEC-002)

```sql
CREATE TABLE IF NOT EXISTS credentials (
    credential_id  UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    name           TEXT        NOT NULL,
    type           TEXT        NOT NULL,   -- ssh-key|ssh-password|snmp|api-token
    encrypted_blob BYTEA       NOT NULL,   -- .NET Data Protection ciphertext
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS credentials_keyset_idx ON credentials (created_at DESC, credential_id);
```
`targets.credential_id` FK (item 5) must be created after this table. `encrypted_blob` never stored/returned in plaintext.

## 7. `users` + `user_sessions` — new (REQ-001/002/023)

```sql
CREATE TABLE IF NOT EXISTS users (
    user_id       UUID        NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    username      TEXT        NOT NULL UNIQUE,
    password_hash TEXT        NOT NULL,             -- PBKDF2 via PasswordHasher
    role          TEXT        NOT NULL DEFAULT 'viewer',  -- admin|viewer
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS user_sessions (
    session_id   TEXT        NOT NULL PRIMARY KEY,  -- high-entropy random
    user_id      UUID        NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen    TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at   TIMESTAMPTZ NOT NULL,
    user_agent   TEXT,
    ip_address   INET
);
CREATE INDEX IF NOT EXISTS user_sessions_user_idx    ON user_sessions (user_id);
CREATE INDEX IF NOT EXISTS user_sessions_expires_idx ON user_sessions (expires_at);
```
Active-sessions admin view (REQ-023) is `SELECT ... FROM user_sessions WHERE expires_at > now()`.

## 8. `audit_log` — new (REQ-027)

```sql
CREATE TABLE IF NOT EXISTS audit_log (
    id          BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    actor       TEXT        NOT NULL,   -- "user:<username>" | "agent:<id>" | "system"
    action      TEXT        NOT NULL,   -- login|logout|agent.approve|target.create|device.merge|...
    target_ref  TEXT,                   -- affected entity id
    detail      JSONB
);
CREATE INDEX IF NOT EXISTS audit_log_keyset_idx ON audit_log (occurred_at DESC, id);
```

## 9. Bootstrap token (DEC-003)

First-run admin bootstrap uses a one-time console token. Store its hash + used flag (small table or a single-row `server_config`):
```sql
CREATE TABLE IF NOT EXISTS bootstrap_token (
    token_hash TEXT        NOT NULL PRIMARY KEY,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    used_at    TIMESTAMPTZ
);
```
Invalidated once the first admin exists.

## 10. Data retention (REQ-025)

No new table. Retention is a scheduled `DELETE FROM facts_history WHERE collected_at < now() - <interval>` (configurable). The schema already notes monthly partitioning of `facts_history` as a future option (deferred, ADR-006); at this scale a bounded periodic delete suffices.

## 11. Report keyset-pagination indexes (REQ-026, API-004)

Each reporting list endpoint paginates by an indexed sort tuple (conventions.md: no offset/limit; every sort tuple index-backed). The existing `proj_*` tables and `facts_history` lack indexes on some of these sort tuples — add them:

```sql
-- hosts report sorts by (hostname, device_id)
CREATE INDEX IF NOT EXISTS proj_systems_hostname_idx
    ON proj_systems (hostname, device);

-- certificate inventory sorts by expiry (find expiring certs)
CREATE INDEX IF NOT EXISTS proj_device_certs_expiry_idx
    ON proj_device_certs (not_after, device);

-- change feed: keyset needs id in the tuple for a stable cursor,
-- not just (collected_at DESC). facts_history_time_idx covers only collected_at.
CREATE INDEX IF NOT EXISTS facts_history_changefeed_idx
    ON facts_history (collected_at DESC, id);
```

Notes:
- `proj_ports`, `proj_containers`, `proj_disks`, `proj_filesystems`, `proj_device_arp`, `proj_security`, `proj_updates`, `proj_services` are keyed by `device` (+ a child key); pagination on `(device, <child_key>)` is served by their existing primary keys. Confirm each report's chosen sort tuple matches an existing PK/index at implementation; add an index only where it does not (the three above are the confirmed gaps).
- Adjust column names (`device` vs `device_id`) to match the actual `proj_*` definitions in `scratch/Schema.sql` (these tables use `device`).

## 12. `excluded_fingerprints` — new (REQ-052, F7 conflict surface)

Backs the admin conflict-resolution `exclude` action (`POST /api/v1/admin/conflicts/{fp_type}/{fp_value}/resolve` with `{action: "exclude"}`). A fingerprint listed here is ignored by future auto-matching in COMP-004, so a known-shared fingerprint (e.g. a shared MAC on a clustered NIC) no longer triggers an auto-merge or a repeated conflict warning.

```sql
CREATE TABLE IF NOT EXISTS excluded_fingerprints (
    fp_type    TEXT NOT NULL,
    fp_value   TEXT NOT NULL,
    excluded_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    excluded_by TEXT,
    PRIMARY KEY (fp_type, fp_value)
);
```
`excluded_by` records the resolving admin (`user:<username>`). COMP-004 consults this table before treating a fingerprint overlap as a merge/conflict signal. The conflict list (`GET /api/v1/admin/conflicts`) excludes any `(fp_type, fp_value)` present here.

## Ordering note

Apply in dependency order: `credentials` (6) before `targets` (5) FK; `agents` (4) before `targets`; `users` before `user_sessions`. The `IF NOT EXISTS` guards make re-application safe.
