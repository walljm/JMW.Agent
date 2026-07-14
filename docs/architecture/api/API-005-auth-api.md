---
id: API-005
path: /api/v1/auth
method: POST
status: draft
---
## Description

User authentication endpoints (browser principals). Server-side sessions with secure `httpOnly` `SameSite=Strict` cookies (constraints #6, no JWT). Session store in PostgreSQL → immediate revocation and the active-sessions admin view (REQ-023).

- `POST /api/v1/auth/login`
- `POST /api/v1/auth/logout`
- `GET /api/v1/auth/me`

(First-run admin bootstrap is a separate one-time flow via DEC-003 console token + bootstrap Razor Page, not part of this versioned API.)

## Request Contract
- `POST /api/v1/auth/login` body `{ "username", "password" }`. Rate-limited.
- `POST /api/v1/auth/logout` — no body; uses the session cookie.
- `GET /api/v1/auth/me` — uses the session cookie.

## Response Contract
- login → `200` + `Set-Cookie: session=...; HttpOnly; Secure; SameSite=Strict` and body `{ "user": { "username", "role": "admin|viewer" } }`. Creates a `user_sessions` row.
- logout → `204` + cookie cleared; deletes the `user_sessions` row.
- me → `200 { "user": { "username", "role" } }` when authenticated.

## Pagination
N/A.

## Error Responses
Uniform envelope. `401 invalid_credentials` (login), `401 unauthorized` (me/logout without a valid session), `429 rate_limited` (login throttling). Login failures are generic (no username-enumeration distinction).

## Cross-References
REQ-001, REQ-002, REQ-023, REQ-027. COMP-008, COMP-007. DEC-003.
