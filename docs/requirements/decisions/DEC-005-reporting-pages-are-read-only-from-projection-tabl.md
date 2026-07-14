---
id: DEC-005
title: Reporting pages are read-only from projection tables; no real-time push
date: 2026-06-04
status: draft
---

## Decision
All reporting views (fleet dashboard, device detail, etc.) read from PostgreSQL projection tables. There is no WebSocket or server-sent events push for live updates. Pages refresh on user action or periodic poll (configurable by the user, default 60 seconds).

## Rationale
Projection tables are updated by the fact ingest pipeline as facts arrive. The latency from device to projection is bounded by the agent collection interval (default 5-10 minutes), so sub-second push from server to browser provides no material benefit. Polling simplifies the server architecture significantly.

## Alternatives Considered
- **WebSocket push on fact arrival**: Adds complexity for marginal user benefit given collection intervals.
- **Server-sent events**: Same assessment.

## Consequences
- Pages may show data that is up to [collection interval] old.
- The UI must display a last-updated timestamp on each page.
- A staleness threshold indicator (visual flag when last-seen is too old) is required.
