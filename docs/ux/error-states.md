---
agent: sdev-ux
date: 2026-06-04
iteration: 2
revision_id: 12
status: draft
---

# Error States — JMW Agent Facts Server

Trigger logic and recovery paths (REQ-028). Visual representation lives in the mockups; this doc is logic + recovery only. Feedback components: `.banner` (.stale/.error/.info), `.toast`, the error page, and per-section inline errors.

| Error | Trigger | What the user can do | Recovery | Mockup |
|-------|---------|----------------------|-----------|--------|
| **Whole-app DB unreachable** | Server cannot reach PostgreSQL | Sees a clean error page (human message, no stack trace / no SQL — REQ-028); error logged server-side | Retry button reloads; comes back when DB recovers | SCR-023 (spec); pattern in `design-system.css` |
| **Reporting page query fails** | A table's data query errors | Sees an inline `.error` banner above that table describing what failed; filter bar still usable | Retry re-runs the query; other pages unaffected | `security.html` / `all-hosts.html` banner pattern |
| **Per-section query fails (Detail)** | One Device/Service Detail section query errors | The affected tab shows an inline error + retry; **all other tabs still render** (REQ-028) | Retry that section; rest of page is unaffected | `device-detail.html` (tab error pattern) |
| **Data stale** | Newest projection data older than 2× the collection interval | Sees a `.stale` banner "Data may be stale — last seen [ts]"; counts/rows still shown but flagged | Informational; auto-clears when fresh data arrives | `dashboard.html`, `all-hosts.html` stale banner |
| **Agent offline / stale heartbeat** | No heartbeat within 2× interval | Agent shows offline/stale status (Agents list + dashboard "stale agents" card) within one refresh cycle (REQ-028) | Investigate the agent; its last-delivered config and last data remain visible | `agents.html` (status pills + row rail) |
| **Config save fails** | Server rejects an agent-config save | Specific inline error; form stays populated; **no partial commit** (REQ-028) | Fix the field and re-save; the pending indicator persists | `agent-config.html` (save error path) |
| **Edit an offline agent** | Saving config for an offline agent | Save succeeds server-side; "changes will be delivered when the agent reconnects" note; pending indicator persists | Delivered on next successful poll; operator is not blocked | `agent-config.html` (pending banner) |
| **Credential delete blocked by references** | Deleting a credential used by ≥1 target | Confirmation dialog lists the referencing targets and requires explicit confirm (REQ-007) | Reassign/remove references or confirm cascade per the dialog | SCR-021 (spec); delete-pattern in conventions |
| **Login failure** | Bad username/password | Generic "Invalid username or password" inline (no account enumeration); password cleared | Retry; repeated failures rate-limited | `login.html` |
| **Promotion connection failure** | Agent can't reach the promoted target | Host reverts to discovered; an Admin-area notification is raised (no email/webhook this iteration — REQ-036); **no passive data lost** | Edit target/credential and re-promote | `all-hosts.html` promote flow + SCR-006 promotion-pending state |
| **Empty / no matching rows** | Filter matches nothing, or no data exists | `.empty` block with explanation + "Clear filters" (or "add your first X") | Adjust filters or add data | `design-system.html` `.empty` pattern |
| **Validation (duration / required)** | Invalid duration ("5x") or missing required field | Inline field error before submit (REQ-005) | Correct the field; submit re-enabled | `agent-config.html` field validation |
| **403 — viewer hits admin URL** | Read-Only Viewer navigates to an Admin route directly | Clear "no access" page + link back to Dashboard (REQ-002); admin nav already hidden | Return to reporting | SCR-024 (spec) |

## Cross-cutting rules

- No raw error messages, stack traces, SQL, constraint names, or DB details are ever shown in the browser (REQ-028, and the layer-separation API guidance).
- Failures use `role="alert"`; stale/informational notices use `aria-live="polite"` (see `accessibility.md`).
- Partial saves are never silently committed; a failed mutation leaves the form populated so no input is lost.
