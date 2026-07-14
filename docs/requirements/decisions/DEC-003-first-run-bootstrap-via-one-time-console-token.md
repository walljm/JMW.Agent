---
id: DEC-003
title: First-run bootstrap via one-time console token
date: 2026-06-04
status: draft
---

## Decision
On first startup, when no admin accounts exist, the server generates a one-time cryptographic token and writes it to the server console log (stdout/stderr). The operator uses this token to access a setup page at /setup and create the initial admin account.

## Rationale
A self-hosted system with no external IdP must bootstrap the first account somehow. Console-log tokens are a well-understood pattern (used by Jenkins, Grafana, etc.) that does not require out-of-band communication channels. It works naturally with systemd journal and Docker logs.

## Alternatives Considered
- **Pre-seeded admin account in config file**: Puts a default password in config, which operators commonly forget to change.
- **Environment variable credential**: Better than a config file but requires the operator to manage secrets at deployment time.
- **No auth on first run**: Unacceptable security risk.

## Consequences
- The operator must check the console log / systemd journal / docker logs on first deployment.
- Documentation must describe this step explicitly.
- The setup endpoint must be disabled after first-account creation.
