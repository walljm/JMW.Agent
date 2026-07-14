---
id: DEC-001
title: Agent config stored server-side; agents poll for their config
date: 2026-06-04
status: draft
---

## Decision
Agent configuration is stored server-side in the database. Agents poll the server for their current configuration at startup and at a periodic interval (e.g., every 5 minutes). The file-based config is a fallback/seed used only when the server is unreachable at startup.

## Rationale
The web UI needs to be the authoritative source of agent configuration. If agents only read from local files, the UI would need a separate mechanism to push changes. Polling is simple, stateless, and works naturally with the existing HTTP-based agent-server relationship.

## Alternatives Considered
- **Server pushes config changes**: Requires agents to maintain a persistent connection or the server to know agent addresses. More complex and conflicts with the agent-initiates-all-communication model.
- **File-only config, UI writes to file**: Only works if the server and agent share a filesystem (single-host deployment). Does not scale to multi-host or containerized deployments.

## Consequences
- Agents need a local file fallback path for when the server is unreachable at startup.
- The server must store and serve per-agent configuration as part of the API.
- Config polling adds a small background HTTP call from each agent.
