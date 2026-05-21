---
agent: sdev-01a-requirements-analyst
date: 2026-04-27
status: draft
---

# Glossary

| Term | Definition |
|---|---|
| **Agent** | The lightweight client binary that runs on a monitored host. Reports its own system metrics to the **Server** and also acts as a **Discovery Observer** for its local subnet. |
| **Server** | The single central process that hosts the dashboard, API, SQLite database, alerting engine, and notification dispatch. Exactly one Server exists per JMW.Agent installation. |
| **Admin** | The single human operator with full access to the dashboard. There is exactly one Admin (see DEC-003). |
| **Registered Agent** | An Agent that has completed registration and is in the 'active' state — its heartbeats and metric reports are accepted and counted. |
| **Pending Registration** | An Agent that has contacted the Server but has not yet been approved (manually or via pre-shared key). Pending agents are visible in a dedicated list and do not affect dashboard summaries. |
| **Pre-Shared Key** | An optional secret configured at first-boot bootstrap. Agents that present this key during registration are auto-approved; agents without it fall through to manual approval. |
| **Heartbeat** | A periodic message from a Registered Agent to the Server that includes current system state and metrics. Default cadence is 5 minutes, configurable per agent. |
| **Discovered Device** | A device observed on the network via ARP or mDNS but **not** running a JMW.Agent. Distinct from a Registered Agent. Identified by MAC address. |
| **Observer** | A Registered Agent that has reported observations of a particular Discovered Device. A single device may have multiple Observers. |
| **Canonical Device Record** | The deduplicated server-side record for a Discovered Device, keyed by MAC, that reconciles observations from multiple Observers into a single 'best-known' representation. |
| **Subnet** | An L2 network segment in Boss's home network, often corresponding to a VLAN. Each Agent observes only its own primary Subnet. |
| **mDNS** | Multicast DNS — a zero-configuration service-discovery protocol used by printers, Chromecasts, and many IoT devices to announce themselves. |
| **OUI Database** | The IEEE Organizationally Unique Identifier database mapping MAC address prefixes to manufacturers. Used for vendor identification of Discovered Devices. |
| **Threshold Rule** | A user-defined alerting rule (e.g., "CPU > 90% for 10 minutes") scoped globally, per-tag/group, or per-device. |
| **Alert** | A condition currently in violation of a Threshold Rule (or built-in offline detection). An Alert has lifecycle: firing → notified (subject to dedup/quiet-hours) → resolved. |
| **Notification Channel** | An outbound destination for alerts (email, Discord webhook, Pushover, Gotify). |
| **Dedup Window** | The configurable interval during which a single firing Alert produces only one notification, regardless of how many evaluation cycles confirm the condition. |
| **Quiet Hours** | A configurable time window during which non-critical notifications are suppressed and rolled up at the end of the window. |
| **Tiered Retention** | The metric storage policy: 7 days at raw resolution, 30 days at 5-minute aggregates, 1 year at hourly aggregates. |
| **Bootstrap** | The first-run flow that creates the initial Admin user and optionally configures the Pre-Shared Key. The bootstrap UI is permanently disabled after completion. |
| **Online-Backup** | SQLite's mechanism for taking a consistent snapshot of a database while it is being written to, used for both manual and scheduled backups. |
| **Cert Pinning** | The practice of an Agent storing the Server's TLS certificate (or its public-key fingerprint) at registration time and refusing to connect to a server presenting any other certificate. Used by REQ-002 / REQ-020 / REQ-052 to prevent man-in-the-middle attacks even if a system trust store is compromised. Cert rotation requires a deliberate re-pinning step on each Agent. |
