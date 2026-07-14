---
id: FLOW-003
title: Promote Discovered Device to Managed
persona: 
status: draft
---

## Goal
The operator brings a discovered host under direct management without losing its passive profile. The most consequential discovery interaction. Admin role only (REQ-036).

## Persona
PERSONA-001 (Admin role) only. The Promote control is not rendered for the Read-Only Viewer.

## Entry point
"Promote" button on a discovered row in All Hosts (SCR-004) or the "Promote to Managed" action on a discovered Device Detail (SCR-006).

## Decision tree
1. Click Promote -> Promotion modal opens (SCR-022) over the current context (row/page preserved behind it).
2. **Step A - Select agent:** choose which registered agent will manage the device (select of online agents).
3. **Step B - Credential:** select an existing stored credential or create a new one inline (type-appropriate: SSH password/key, SNMP, etc., REQ-007). Secret entry is write-only.
4. **Step C - Confirm target:** confirm/adjust the IP or hostname the agent will connect to (pre-filled from the discovered facts).
5. **Final confirm:** modal restates the consequence -- "Agent X will begin SSH collection against host Y using credential Z. This creates a remote target and starts live collection. Existing passive data is kept." Operator confirms.
6. On confirm: server creates a RemoteTarget (REQ-006) bound to the agent + credential; action is audit-logged (REQ-027). Toast: "Promotion queued -- waiting for first collection."
7. Status transitions to managed when the first agent-direct fact batch arrives (REQ-036). Until then the host shows a "promotion pending" indicator; passive facts remain visible throughout.

## Data requirements per step
- Step A: list of registered/online agents.
- Step B: list of stored credentials (label/type only, never secrets).
- Step C: discovered device's known IP(s)/hostname(s) to pre-fill.
- Step 6-7: created RemoteTarget; later, first agent-direct fact batch.

## Error / edge paths
- Agent cannot reach the target after promotion (bad credential / unreachable) -> host reverts to discovered, an Admin-area notification is raised (REQ-036, no email/webhook this iteration). **No passive data is lost** (REQ-035/036). Recovery: operator edits the target/credential and re-promotes.
- Operator cancels the modal -> nothing is created; returns to the originating row/page with state intact.
- Save fails server-side -> specific error shown; no partial RemoteTarget committed (REQ-028).

## Symmetry / recovery (undo limitation, acknowledged)
Promotion is NOT freely reversible: managed -> discovered is not a normal transition (REQ-030) and would require manual agent de-enrollment (out of scope). The design relies on the pre-commit confirmation (prevention) and the non-destructive data model (no data lost on failure/regret) rather than an undo. The automatic revert-to-discovered on connection failure is the safety net. This limitation is stated to the operator at the confirm step. (Documented intentional asymmetry per Phase 3 symmetry rule.)

## Screens touched
SCR-004 / SCR-006 -> SCR-022 (promotion modal) -> back to origin.
