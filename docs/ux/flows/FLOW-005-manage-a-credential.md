---
id: FLOW-005
title: Manage a Credential
persona: 
status: draft
---

## Goal
The operator creates, rotates, renames, or deletes a stored credential used by agents to reach remote targets. Episodic (rotation periods). Admin only. Secrets are write-only and never displayed (REQ-007).

## Persona
PERSONA-001 (Admin role) only.

## Entry point
Admin > Credentials (SCR-021).

## Decision tree
1. Credentials list (SCR-021): Label, Type, Created At, Last Used At, Referenced By (count of targets). No secret values shown anywhere.
2. Operator chooses an action:
   - **Create:** modal -> enter Label, choose Type (SSH password / SSH key / SNMP v1-v2c / SNMP v3 / HTTP bearer / HTTP basic). Type selection reveals the appropriate secret field(s). Submit -> encrypted at rest (REQ-007/DEC-002), audit-logged.
   - **Rename:** modal pre-filled with Label only (no secret); save new label, references preserved.
   - **Rotate:** modal -> enter a NEW secret value (the existing secret is never shown / never pre-filled); Label and all target references preserved.
   - **Delete:** if Referenced By > 0, confirmation dialog lists the referencing targets and requires explicit confirm (REQ-007). If unreferenced, a simple delete confirm.
3. On any mutating action: toast confirms; the list refreshes; the operation is audit-logged (REQ-007/027).

## Data requirements per step
- Step 1: credential metadata only (label, type, created/last-used, reference count) -- never secret material in any response body (REQ-007).
- Delete: list of targets referencing the credential.

## Error / edge paths
- Save fails -> specific error, form populated, no partial commit (REQ-028).
- Type-appropriate secret missing -> inline validation.
- Attempt to delete a referenced credential without resolving references -> blocked behind the confirmation listing the dependents.

## Symmetry / recovery
Anything created can be deleted (with reference-aware confirmation). Rotation is reversible only by entering yet another secret (no recovery of the prior secret -- by design; secrets are write-only). This irreversibility is inherent to secret handling and acceptable.

## Screens touched
SCR-021 (with create/rename/rotate/delete modals).
