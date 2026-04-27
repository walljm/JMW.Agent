---
id: DEC-005
title: SQLite as the sole datastore
date: 2026-04-27
status: draft
---

## Context

A central monitoring server with metrics, events, sessions, device inventory, and discovery state. Server may run on a small VM or even a Raspberry Pi.

## Decision

All persistent state lives in a single SQLite database file. No separate database process (PostgreSQL, MySQL, time-series DB, etc.).

## Rationale

- Single-binary deploy: no DB to install, configure, or back up separately.
- One file = trivial backup story (copy the file).
- SQLite handles single-writer monitoring workloads well; reads are concurrent.
- Tiered downsampling keeps file size bounded for the expected scale (25-75 devices).

## Consequences

- Architect must select a SQLite-friendly access pattern (WAL mode, single writer goroutine, etc. — implementation concern).
- Time-series queries are SQL with appropriate indexing, not a specialized engine.
- File-locking semantics rule out NFS/SMB hosting of the DB.
- If load ever exceeds SQLite's single-writer ceiling, that's a future redesign trigger.
