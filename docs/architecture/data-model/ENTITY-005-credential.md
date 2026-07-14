---
id: ENTITY-005
name: Credential
status: draft
---
## Fields
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| credential_id | UUID (PK) | yes | Identity. `gen_random_uuid()`. |
| name | TEXT | yes | Operator-facing label (REQ-007). |
| type | TEXT | yes | `ssh-key` \| `ssh-password` \| `snmp` \| `api-token`. |
| encrypted_blob | BYTEA | yes | Secret material encrypted with .NET Data Protection (DEC-002, D10). Never stored or returned in plaintext. |
| created_at | TIMESTAMPTZ | yes | Default `now()`. |
| updated_at | TIMESTAMPTZ | yes | Default `now()`. |

## Relationships
- 1:N → Target (ENTITY-004) via `credential_id` (a credential may be reused across targets).

## PII Fields
`encrypted_blob` contains sensitive secrets (SSH keys, passwords, SNMP community strings, API tokens). **Encrypted at rest** (constraints #8). Never logged. Decrypted only server-side when delivering a target's config to its authenticated agent. The admin API never returns plaintext (write-only / metadata-only reads, REQ-007).

## Ownership
Owned by Server.Web admin (COMP-007); encryption/decryption via Server.Auth Data Protection (COMP-008).

## Migration Strategy
**New table** in iter-2. `CREATE TABLE IF NOT EXISTS credentials (...)` with `encrypted_blob BYTEA NOT NULL`. No backfill. Rollback: `DROP TABLE credentials` (and clear `targets.credential_id`). Validation: store + retrieve a credential round-trip; assert at-rest ciphertext and that decryption requires the persisted Data Protection key ring (D8).

Note: `CreateCredential`/`RotateSecret` trim the secret server-side before encrypting — a
stray leading/trailing space or newline from copy-paste otherwise silently produces a stored
secret that never authenticates, with no error at save time.
