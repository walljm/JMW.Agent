---
agent: sdev-01a-requirements-analyst
date: 2026-04-27
status: draft
---

# Constraints, Assumptions, and Out-of-Scope

## Constraints

### Deployment & Platform
- **Server platforms:** Linux x86_64 and Linux ARM (Raspberry Pi). No other server platforms supported.
- **Agent platforms (must-have):** Linux x86_64, Linux ARM, and macOS (recent). These are Boss's primary fleet members and ship every release (REQ-003).
- **Agent platforms (should-have):** Windows x86_64. Windows is a should-have rather than must-have because Boss uses it infrequently (REQ-053). Windows-only failures do not block a release.
- **Out of scope:** FreeBSD and OpenBSD agent support. Boss does not run BSD hosts; supporting them would be gold-plating (see R-1).
- **Resource sensitivity:** The agent must be runnable on a Raspberry Pi without dominating its resources. No hard footprint targets, but agent resource use is observed and treated as a quality concern. Resource-constrained hosts can disable individual subsystems (REQ-002 AC #5).
- **Single-binary delivery:** Both server and agent must be self-contained executables with no runtime dependencies (no Node, no Python, no JVM).
- **Self-hosted only:** No SaaS, no cloud APIs, no external account requirements (DEC-004).

### Network Topology
- **Multi-VLAN home network:** The system must work when agents are on isolated subnets (IoT VLAN, guest VLAN, etc.) with strict egress rules allowing only the path to the server.
- **Distributed discovery:** Network discovery is performed by each agent on its own subnet, not by a central scanner (DEC-001).
- **Agents do not need to reach each other** — the only required network path is agent → server.

### Data
- **SQLite is the sole datastore** (DEC-005). No external DB process.
- **Tiered retention:** 7 days raw / 30 days at 5-minute aggregates / 1 year at hourly aggregates.
- **Backups stay local by default**; remote sync (rsync/scp) is opt-in.

### Security
- **Single-user, single admin** (DEC-003). No multi-user, no RBAC.
- **Session cookie auth, not JWT** (DEC-002).
- **Mutual authentication and encryption between agent and server** is required (REQ-020).
- **Dashboard is HTTPS by default** with a self-signed cert generated at first boot, sharing the cert with agent transport (REQ-052).
- **Password recovery is a server-side CLI operation** (REQ-006). No email-based recovery.

### Implementation Discipline
- **Strict third-party dependency policy** (DEC-006). Stdlib first; non-stdlib dependencies require justification.

### Localization
- **English-only.** No internationalization or localization required.

## Assumptions

- The server runs on a host Boss controls (filesystem access for CLI password reset and backup management is available).
- The home network has a writable LAN — i.e., agents can open outbound TLS connections to the server's address.
- Boss is willing to operate the system: approve agents, configure thresholds and notification channels, occasionally check the dashboard.
- 25–75 devices total (agents + discovered combined) is the expected steady-state.
- A non-zero portion of devices are on isolated subnets (the IoT VLAN is a primary motivator for distributed discovery).
- Browsers used to access the dashboard are modern (Chrome, Firefox, Safari latest). Legacy IE / pre-ES2017 browsers are not supported.
- Email, Discord, Pushover, and Gotify are the relevant notification channels. SMS, Slack, PagerDuty, etc. are not in scope.

## Out of Scope

The following were considered and explicitly excluded — either deferred indefinitely or noted as 'never for this project':

- **FreeBSD and OpenBSD agent support.** Out of scope (see Agent platforms above and R-1). Not on Boss's fleet; supporting these OSes would be gold-plating.
- **Wake-on-LAN.** Deferred.
- **Remote command execution / SSH proxy / remote terminal.** Deferred. Boss explicitly does not want a remote-shell attack surface in a home tool.
- **Automated remediation** (auto-restart services, auto-clear disk, auto-reboot devices). Deferred. JMW.Agent observes and notifies; Boss takes action.
- **Multi-user / role-based access control.** Out of scope (DEC-003). Single user only.
- **Multi-tenant / multi-household.** Out of scope.
- **SNMP monitoring.** Out of scope. Switch/AP introspection via SNMP is not supported; topology is inferred from agent observations only.
- **Internationalization / localization.** Out of scope. English only.
- **Mobile app.** Out of scope for now — phone access is via the responsive dashboard.
- **Cloud-hosted / SaaS deployment.** Out of scope. Self-hosted only.
- **Public internet exposure of the dashboard.** Out of scope as a primary use case. The dashboard is intended for LAN/VPN access; if Boss exposes it publicly, that's his call but not a design target.
- **Windows agent privileged operations** that require admin rights (e.g., raw ICMP). Best-effort fallback to TCP probes; explicit 'unsupported' rather than crashing.
- **Compliance regimes** (HIPAA/PCI/SOC2/GDPR). Personal household use, no regulated data.
