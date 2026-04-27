---
id: DEC-004
title: Self-hosted only; no SaaS or cloud dependencies
date: 2026-04-27
status: draft
---

## Context

JMW.Agent is intended to replace commercial monitoring tools and SaaS dashboards.

## Decision

The system runs entirely on Boss's home network. No cloud services, no SaaS APIs, no external account requirements. Outbound network use is limited to user-configured notification channels (email SMTP, Discord webhook, Pushover, Gotify).

## Rationale

- Privacy: data about devices, network topology, and traffic stays in the household.
- Cost: zero recurring spend.
- Resilience: a WAN outage doesn't disable monitoring of the LAN itself.

## Consequences

- Updates are user-driven (server holds latest binary; agents pull on heartbeat).
- No telemetry shipped from server to any author/maintainer.
- Backups are local by default; remote sync (rsync/scp) is opt-in and user-configured.
