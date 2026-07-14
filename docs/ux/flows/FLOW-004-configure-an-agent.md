---
id: FLOW-004
title: Configure an Agent
persona: 
status: draft
---

## Goal
The operator sets up or adjusts an agent's collection behavior and remote targets through the UI, without editing config files (project success criterion 1). Low frequency, high consequence. Admin only.

## Persona
PERSONA-001 (Admin role) only.

## Entry point
Admin > Agents (SCR-019) -> click an agent.

## Decision tree
1. Agent List (SCR-019) shows all agents with Name, Zone, last heartbeat, online/offline status. Click an agent -> Agent Detail hub (SCR-020).
2. Agent Detail is a hub-and-spoke page; operator chooses a spoke in any order:
   - **Configuration:** edit Name, Zone, Interval (human-readable duration like "5m"), MaxConcurrency (REQ-005). Advanced fields grouped but visible. Explicit Save. UI shows pending-vs-last-delivered config (REQ-005).
   - **Targets:** one table of targets, spanning both device-style (ssh, snmp, bacnet, modbus, google-wifi) and service-style (technitium-dns, home-assistant) collectors; Add/Edit opens a modal (endpoint, collector type, credential reference); Delete confirms; Discover targets… surfaces already-discovered candidates (REQ-006).
   - **Collectors:** list of collectors each with an enable/disable toggle (REQ-008) and an optional per-collector frequency override defaulting to the agent interval (REQ-009).
3. On Save (config) or modal submit (target): change persisted server-side, audit-logged (REQ-005/027), delivered to the agent on its next config poll (DEC-001). Toast confirms; pending indicator clears when the agent acknowledges.

## Data requirements per step
- Step 1: agent list + heartbeat/status (REQ-003/004).
- Step 2 Configuration: current agent-level settings + last-delivered snapshot.
- Targets: target list + available credential labels.
- Collectors: collector roster + enabled state + per-collector frequency.

## Error / edge paths
- Save fails -> specific error shown, form stays populated, no partial commit (REQ-028).
- Invalid duration format -> inline validation before submit.
- Agent offline -> changes are saved server-side and queued; UI shows config is pending delivery (delivered on next successful poll). Operator is not blocked from editing an offline agent's config.

## Symmetry / recovery
Anything created (target, collector-enable) can be deleted/disabled. Config edits are reversible by re-editing (no undo stack needed; low-consequence, REQ-005). Delete of a target/agent confirms.

## Screens touched
SCR-019 -> SCR-020 (with target/credential modals).
