---
id: REQ-001
title: Server runs as a single self-contained binary
priority: must-have
category: deployment
status: draft
depends_on: []
trace_to: []
---

## Description

The JMW.Agent server is delivered as a single executable binary with no runtime dependencies beyond the OS. All assets (HTML/CSS/JS, default templates, embedded migrations) ship inside the binary. The server creates and manages its SQLite database file on first run.

## Acceptance Criteria

1. `./jmw-server` (or platform equivalent) starts the server with no separate install or package manager step.
2. Server runs on Linux x86_64 and Linux ARM (Raspberry Pi).
3. No external runtime is required (no Node, no Python, no JVM).
4. Configuration is via flags, env vars, or a single config file — no scattered system files.
5. First run with an empty data directory creates the SQLite DB and initializes schema automatically.
