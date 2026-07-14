---
id: FLOW-001
title: Daily Fleet Scan
persona: 
status: draft
---

## Goal
The operator answers "is anything wrong across the fleet?" and jumps straight to any problem. Highest-frequency flow (every login + periodic check).

## Persona
PERSONA-001 (Network Operator) primary; PERSONA-002 (Read-Only Viewer) performs the same flow read-only.

## Entry point
Login redirect, or sidebar Dashboard.

## Decision tree
1. Land on Fleet Dashboard (SCR-003). Auto-refresh on (default 60s); last-updated timestamp visible.
2. Scan summary cards for any non-zero / status-colored count.
   - **All counts nominal** -> done; operator closes or sets refresh and walks away.
   - **A card is hot** (e.g., "12 expiring certificates") -> click the card.
3. Land on the matching reporting page **already filtered** to the offending set via the URL filter convention (e.g., /certs?status=expiring -> SCR-010 filtered).
4. Scan/sort the filtered rows; identify the specific entity needing attention.
   - Need more detail -> click the row -> entity Detail page (SCR-006 / SCR-008). (This is the hand-off into the Investigate flow.)
   - Enough info to act here -> proceed offline (e.g., go rotate the cert).

## Data requirements per step
- Step 1: all dashboard summary counts (REQ-010 list), last-updated timestamp, staleness flags.
- Step 3: filtered reporting rows matching the URL filter param.

## Error / edge paths
- Dashboard query fails -> the affected card shows an inline error; other cards still render (REQ-028). Operator can retry.
- Data stale (older than 2x interval) -> "Data may be stale" banner on dashboard and on the drilled-into page (REQ-028); counts still shown but flagged.
- Filtered page returns 0 rows (card count was from a prior refresh now resolved) -> empty state explaining no matching items, with a clear-filter link.

## Symmetry / recovery
Read-only flow; nothing to undo. Operator can always clear filters to see the full reporting set, and the sidebar returns to Dashboard from anywhere.

## Screens touched
SCR-003 -> any of SCR-009..SCR-015 (filtered) -> optionally SCR-006/SCR-008.
