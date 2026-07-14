---
agent: sdev-requirements
date: 2026-06-04
status: draft
---

# Glossary

## Domain Terms

**Agent**
A running instance of the JMW Agent process on a host. One agent may collect from the local host and from multiple remote targets. Identified by a stable UUID assigned at first registration.

**Collector**
A named plugin within an agent that collects a specific category of data (e.g., `HardwareCollector`, `CertScanCollector`). Collectors can be enabled/disabled independently.

**Fact**
The atomic data unit. A single piece of information about an entity at a point in time. Represented as `{ Id, AttributePath, KeyValues, Kind, ValueStr/Long/Double, CollectedAt }`.

**Fact ID**
A path-syntax string uniquely identifying a fact for a specific entity instance. Example: `Device[router-1].Interface[eth0].Speed`.

**Attribute Path**
The structural form of a Fact ID with empty brackets — e.g., `Device[].Interface[].Speed`. Used for efficient queries across all instances.

**Fact Batch**
A collection of facts submitted by an agent to the server for a single device in one HTTP POST.

**Projection Table**
A database table representing the current state of entities (devices, services, containers, etc.), maintained by the server's projection pipeline as facts arrive.

**Device**
A physical or virtual network node that can be communicated with directly. Has a stable server-assigned UUID derived via fingerprinting.

**Target**
A remote device or service that an agent collects data from. Configured per-agent with an endpoint (bare host/IP for device-style collectors, a full URL for service-style ones), a collector type (e.g. `ssh`, `snmp`, `bacnet`, `modbus`, `google-wifi`, `technitium-dns`, `home-assistant`), and an optional credential. Device-style and service-style targets share one table and one admin UI — the collector type alone determines whether an `IDeviceCollector` or `IServiceCollector` handles it.

**Service**
A logical service identity (e.g., a step-ca instance, a DNS server, a DHCP server). Has a stable UUID managed by the ServiceRegistry.

**Fingerprint**
A stable identifier for a device used to assign and maintain its UUID across address or hostname changes. Types include chassis serial, SSH host key, MAC address, etc.

**Heartbeat**
A periodic signal from a running agent to the server indicating it is alive and active. Includes timestamp of last collection.

**Zone**
An operator-defined grouping label for agents/devices (e.g., `home`, `office`, `datacenter-1`).

**Projection Pipeline**
The server-side component that receives fact batches and updates projection tables. Called `FactIngestPipeline` in the codebase.

**EntityStateCache**
An in-memory cache on the server that prevents redundant projection table writes when entity state hasn't changed.

**Change Feed**
The time-ordered audit trail of all fact value changes, queryable from the `facts_history` table.

**SMART**
Self-Monitoring, Analysis and Reporting Technology — a disk health monitoring standard. The system collects SMART data to report disk health status.

**Step CA**
A certificate authority (CA) run by the Smallstep `step-ca` product. The system monitors step-ca services and their issued certificates.

**Technitium**
A DNS/DHCP server product. The system collects DNS zone, record, and DHCP lease data from Technitium instances.

**Credentials Store**
The server-side secure store for SSH passwords/keys, SNMP community strings, API tokens, and other secrets used by agents to access remote targets.

**Collection Interval**
The period between successive data collection runs for an agent. Configurable per-agent, with per-collector overrides.

**Concurrent Collectors**
The maximum number of collectors an agent runs simultaneously. Configurable as `MaxConcurrency` in agent config.

**Delta Tracking**
The collector-side mechanism that transmits only changed facts, reducing server write load.

**Fleet**
The complete set of devices and services monitored by all registered agents.

**Posture**
A security posture metric — the aggregate of firewall state, antivirus state, TPM presence, Secure Boot state, and pending security updates across the fleet.
