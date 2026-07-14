---
id: COMP-008
name: Server Auth
status: draft
---
## Responsibility

Authentication, authorization, and secret protection (`Server.Auth`, hosted in `Server.Web`). Covers **two distinct principals** cleanly (critic check #4):

**1. User sessions (REQ-001, REQ-002)** — browser principals:
- Server-side sessions with secure, `httpOnly`, `SameSite=Strict` cookies (constraints #6, no JWT; REQ-001 AC). Session store in PostgreSQL (`user_sessions`), so logout/revocation is immediate and the active-sessions admin view (REQ-023) is a simple query.
- Local user store (`users` table); password hashing with ASP.NET Core `PasswordHasher` (PBKDF2). No external IdP (constraints #5).
- **RBAC** (REQ-002): roles `admin` (full) and `viewer` (read-only). Enforced by authorization policies on Razor Pages/handlers and JSON endpoints.
- **First-run bootstrap** (DEC-003): a one-time console token printed at first start lets the operator create the initial admin via the bootstrap page; the token is invalidated after first admin creation.

**2. Agent API keys (REQ-003, REQ-004, D4)** — machine principals:
- Open self-registration with admin approval. On `register`, the server stores the agent as `pending` and issues an API key; only its **hash** (`api_key_hash`, salted) is persisted, never the plaintext (plaintext returned once in the register response).
- Agent requests are authenticated by `Authorization: Bearer <api_key>`; middleware looks up the agent by key hash, attaches `agent_id`, and rejects if not `approved` (403 `not_approved`).
- Approval/disable flips `agents.status`; disabling immediately rejects subsequent agent calls.

**Credential encryption (REQ-007, DEC-002, D10)**: collection-target credentials (SSH keys/passwords, SNMP strings, API tokens) are encrypted with the **.NET Data Protection API** before storage as `bytea`. The key ring is persisted to a volume path (D8) so restarts/redeploys can still decrypt. Decryption happens only when an agent fetches its assigned targets/credentials over the authenticated channel.

## Interfaces

- **→ COMP-007 Web (in-process middleware)**: cookie-session auth, RBAC policies, agent API-key auth.
- **→ COMP-009 Database (in-process)**: `users`, `user_sessions`, `agents` (api_key_hash, status), `credentials` (encrypted_blob).
- **Data Protection key ring**: persisted to a configured directory (volume-mounted, D8).

## Dependencies

- COMP-009 Database
- DEP: ASP.NET Core Data Protection (framework), ASP.NET Core Identity PasswordHasher (framework)

## Integration Test Boundaries

- **Session lifecycle (HTTP+DB)**: login issues a session row + httpOnly cookie; logout deletes the row; a request with the now-deleted session cookie is rejected (immediate revocation).
- **RBAC enforcement (HTTP)**: viewer attempts an admin write (approve agent / edit target); assert 403 and no state change.
- **Agent key issuance + approval gate (HTTP+DB)**: register → `pending`; agent `facts` call → 403 `not_approved`; admin approve → next call succeeds. Assert only the key hash is stored.
- **Credential round-trip (DB)**: store an SSH key; assert ciphertext in `credentials.encrypted_blob`; assert decryption succeeds after a simulated key-ring reload from the persisted path; assert decryption fails if the key ring is absent (proves at-rest encryption).
