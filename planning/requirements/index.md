---
agent: sdev-01a-requirements-analyst
date: 2026-04-27
status: draft
---

# JMW.Agent — Requirements

## Project Summary

**JMW.Agent** is a self-hosted home network monitoring and management dashboard. A central server runs on Boss's home LAN; lightweight agent binaries run on the servers, desktops, and Raspberry Pis around the house. Each agent reports system metrics about its own host **and** acts as a network sensor for its local subnet, scanning for agentless devices (printers, IoT, switches, phones) via ARP and mDNS. The server collates everything into a single dashboard with live status, historical metrics, alerting, and notifications.

This is a deliberate rewrite of an earlier .NET prototype, now being implemented in Go for cross-platform single-binary deployment. The audience is exactly one person — the home-lab operator (see [PERSONA-001](personas/PERSONA-001-home-lab-operator-boss.md)).

## Project-Level Success Criteria

The project is "done enough to live with" when all of the following are true:

1. **It replaces the existing tools.** Boss can decommission Netdata/Prometheus and any ad-hoc scripts because JMW.Agent covers their useful functionality.
2. **It's trustworthy.** When a critical device goes offline, Boss finds out via notification within the alert-latency budget — and false alarms are rare enough that he doesn't start ignoring them.
3. **It's complete enough to be useful.** All MVP-priority requirements are implemented and the should-have requirements are implemented except where deferred deliberately.
4. **It runs unattended.** The server runs for at least one month without manual intervention (no manual restarts, manual DB cleanup, or manual log rotation).
5. **It works on Boss's actual network.** Multi-VLAN home topology is exercised end-to-end: at least one agent on the IoT VLAN successfully reports through to the server on the main VLAN, and the IoT/Chromecast/printer devices on that VLAN show up in the discovery views.
6. **The Pi can run it.** A Raspberry Pi running an agent stays well within reasonable resource bounds while doing its job (system metrics + subnet scanning + mDNS).

## Scope: MVP vs Full Vision

### MVP (must-have for first usable release)

The minimum to replace the existing tools for daily use:

- Server + Agent binaries with single-binary deploy
- Cross-platform agent (Linux x86, Linux ARM, macOS as must-have; Windows is should-have via REQ-053; BSD is out of scope)
- First-boot admin bootstrap, session-based auth, CLI password recovery
- Agent self-registration with server-side approval, optional pre-shared key auto-approve, pending expiration
- Encrypted, mutually authenticated agent transport
- Dashboard HTTPS by default with self-signed cert generated at first boot (REQ-052)
- Multi-VLAN/multi-subnet operation
- Basic system info collection (CPU, memory, disk, network, OS)
- Configurable heartbeat (default 5 min, configurable up to ~5 sec floor)
- Offline detection (5 missed heartbeats default)
- Client list view + client detail view + dashboard summary cards (with documented MVP scope: discovery / classification / sparkline-dependent sections appear once their should-have REQs ship)
- Device grouping and tagging
- Event log / activity feed
- Dark-mode-default responsive UI (desktop + mobile + tablet)
- SQLite persistence
- Server resilience to agent/network failures

REQ IDs: REQ-001 through REQ-009, REQ-011 through REQ-020, REQ-044, REQ-050, REQ-052. (REQ-010 — new-agent registration notifications — is should-have. REQ-053 — Windows agent — is should-have.)

### Full vision (should-have / nice-to-have)

The full project as Boss conceives it today, all of which is in scope but not all of which is gating for the first release:

- **Metrics & alerting:** historical metrics with sparklines, tiered retention, configurable thresholds, notification channels (email/Discord/Pushover/Gotify), per-alert dedup, quiet hours, uptime/reboot history, bandwidth, disk I/O, SMART, Docker, listening services, OS update status (REQ-021–033)
- **Network discovery:** distributed ARP+mDNS scanning, server-side dedup, DNS resolution tracking, DHCP/rogue detection, latency monitoring, topology map, IoT/printer/Cast discovery, three discovery views, auto-classification, firmware tracking (REQ-034–043)
- **Operations:** new-agent registration notifications, agent auto-update (with embedded-trust-root signature verification + auto-rollback), manual backup download, scheduled snapshots, optional remote sync, restore from backup (CLI + UI, should-have to verify backups actually work), event-log export with row cap, Windows agent (REQ-010, REQ-045–049, REQ-051, REQ-053)

### Explicitly out of scope (now and possibly forever)

- Wake-on-LAN
- Remote command execution / remote terminal / SSH proxy
- Automated remediation (auto-restart services, auto-clear disks)
- Multi-user / RBAC / multi-tenant
- SNMP-based monitoring
- Internationalization (single-locale, English-only)

## Table of Contents

| Document | Purpose |
|---|---|
| [glossary.md](glossary.md) | Domain terms and definitions |
| [stakeholders.md](stakeholders.md) | Stakeholders and decision-makers |
| [constraints.md](constraints.md) | Constraints, assumptions, and out-of-scope items |
| [quality-standards.md](quality-standards.md) | Coverage thresholds, performance budgets, retention policies |
| [risks.md](risks.md) | Tensions and trade-offs identified during requirements |
| `personas/` | User personas (one per file, `PERSONA-NNN-*.md`) |
| `decisions/` | Recorded decisions (`DEC-NNN-*.md`) |
| `REQ-NNN-*.md` | One file per requirement at the top level of this directory |

To enumerate requirements use `artifact records list --type=req --format=ids`.
