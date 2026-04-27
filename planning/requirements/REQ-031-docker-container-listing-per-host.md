---
id: REQ-031
title: Docker container listing per host
priority: should-have
category: functional
status: draft
depends_on: []
trace_to: []
---

## Description

When Docker is detected on a host, the agent reports the list of running containers (name, image, status, started-at, ports, restart count) on each heartbeat. The detail view shows the container list and basic per-container metrics where available.

## Acceptance Criteria

1. Container listing appears only on hosts where Docker is detected.
2. Per-heartbeat updates show currently running, stopped, and recently restarted containers.
3. Container restart-count increases are surfaced as events in the activity feed.
4. Hosts without Docker are unaffected (no errors, no empty Docker section in their detail view).
