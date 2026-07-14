---
agent: sdev-architecture
iteration: 2
date: 2026-06-04
status: draft
---

# Security Architecture

Two principal types are authenticated by distinct mechanisms; both are owned by COMP-008 (Server.Auth) and enforced as ASP.NET Core middleware so no endpoint hand-rolls auth (critic check #4).

## 1. User authentication (browser principals) — REQ-001, REQ-002

- **Mechanism**: server-side sessions. On login, a session row is written to `user_sessions` and a cookie is set: `HttpOnly; Secure; SameSite=Strict`. No JWT (constraints #6). Session id is a high-entropy random token; the cookie carries the id only, server holds the state.
- **Revocation**: deleting the `user_sessions` row immediately invalidates the session (logout, admin force-logout, password change). This also powers the active-sessions admin view (REQ-023).
- **Password storage**: ASP.NET Core `PasswordHasher<TUser>` (PBKDF2 with per-user salt). No plaintext, no reversible storage.
- **No external IdP** (constraints #5); local `users` table only.
- **Login hardening**: rate-limit login attempts (`429 rate_limited`); generic failure message (no username enumeration); CSRF protection via ASP.NET Core anti-forgery tokens on all state-changing form posts (Data Protection-backed).

### RBAC (REQ-002)
- Roles: `admin` (full), `viewer` (read-only).
- Enforced by authorization policies: `RequireAdmin` on all `/api/v1/admin/*` and device merge/promote (API-002/003); reporting + device read endpoints allow both roles. Razor Pages hide admin-only actions for viewers and the server still enforces (defense in depth) — a viewer POST to an admin route returns `403 forbidden`.

### First-run bootstrap (DEC-003)
- On first start with no users, the server prints a **one-time console token**. The operator opens the bootstrap page, supplies the token, and creates the initial admin. The token is single-use and invalidated once an admin exists. No default credentials shipped.

## 2. Agent authentication (machine principals) — REQ-003, REQ-004, D4

- **Open self-registration with admin approval.** On first start the agent generates its own `agent_id` (UUID) and calls `POST /api/v1/agent/register`. The server creates the agent as `status=pending` and issues an **API key**, returning the **plaintext exactly once**; only a salted hash (`api_key_hash`) is persisted.
- **Authentication**: every agent call carries `Authorization: Bearer <api_key>`. Middleware looks up the agent by key hash, attaches `agent_id`, and verifies the body `agent_id` matches. Mismatch → `401`.
- **Approval gate**: facts/heartbeat from a non-`approved` agent return `403 not_approved` (mirrors the Go app). Admin approval flips `status=approved`; disable flips `disabled` and immediately rejects further calls.
- **No pre-shared tokens** (D4). API-key rotation: re-issue via admin (future); not required this iteration.

## Credential protection at rest — REQ-007, DEC-002, D10

- Collection-target secrets (SSH keys/passwords, SNMP community strings, API tokens) are encrypted with the **.NET Data Protection API** (DEP-004) and stored as `credentials.encrypted_blob BYTEA`. Plaintext is never stored, logged, or returned by the API (admin reads return metadata only).
- **Key ring persistence (D8)**: the Data Protection key ring is persisted to a configured **volume path** (`/var/lib/jmw/keyring` / mounted volume), never in-container ephemeral storage — otherwise stored credentials become undecryptable after restart. The key ring directory is itself protected at the OS/volume level (restricted permissions; optionally key-encrypted-at-rest where the platform supports it — confirm at implementation, DEP-004).
- Decryption occurs server-side only when delivering a target's config to its authenticated, approved agent.

## Transport

- **UI**: HTTPS in deployment (assumption #8); secure cookies require it.
- **Agent→server**: HTTPS desirable; not a hard requirement this iteration (assumption #8 — trusted internal network). API keys assume confidentiality of the channel; document the HTTPS recommendation in deployment.md.

## Audit (REQ-027)

`audit_log` records security-relevant events: login/logout, agent register/approve/disable, target/credential create/delete, device merge/promote. Append-only; surfaced in the admin UI.

## Threat notes / accepted risks

- Single Postgres + single server instance are SPOFs — accepted (non-HA monitoring tool, out-of-scope SLA/HA; ADR-005/006).
- Auto-merge (ADR-002) could merge two devices sharing a duplicated serial — mitigated by normalization rejecting weak/placeholder identifiers and by manual re-split (REQ-053); every merge is audited.
- Agent API key in a plaintext channel is exposed if HTTPS is not used — mitigated by trusted-network assumption; HTTPS recommended.
