---
id: COMP-002
name: Agent Passive Discovery
status: draft
---
## Responsibility

Background passive-discovery subsystem running **inside the Agent process** (COMP-001), per ADR-003 (D3) and D5. Distinct from the main collection cycle: it is event-driven, not interval-driven.

- **Persistent listeners** run as long-lived background tasks: mDNS/Bonjour (REQ-031), SSDP/UPnP (REQ-039), WS-Discovery (REQ-040), LLMNR/NetBIOS (REQ-034), and a passive ARP watcher (REQ-033). Active discovery probes (SNMP REQ-042, Roku ECP REQ-050, Google Cast REQ-048, AirPlay REQ-047, IPP REQ-049) run on the collection interval, not as listeners.
- **Event-driven mini-batches**: when a listener observes a device, it emits a small `{fingerprints[], facts[]}` batch outside the main cycle. These flow through the same `POST /api/v1/agent/facts` endpoint and the same delta tracker (keyed by the discovery source, e.g. `passive:mdns:<host-key>`).
- **Privilege-aware with graceful degradation** (D5): raw-socket collectors (passive ARP monitor, NBNS/LLMNR listener) attempt to open raw sockets at startup. If unavailable (no `CAP_NET_RAW` / not root), they fall back to snapshot ARP collection and emit a structured warning rather than crashing. Capability state is reported on heartbeat so the UI can surface degraded discovery (REQ-038 confidence/source tracking).
- Discoveries are first-class **discovered devices** (REQ-029): the server creates a device with `management_status = discovered` and records the discovery source (REQ-038).

## Interfaces

- **Internal → Agent (in-process)**: emits `FactBatch` mini-batches to the Agent's submission queue. Shares the Agent's API key and `agent_id`.
- **Network (inbound, passive)**: listens on multicast groups (mDNS 224.0.0.251:5353, SSDP 239.255.255.250:1900, LLMNR 224.0.0.252:5355, WS-Discovery 239.255.255.250:3702) and raw L2 (ARP). No inbound control plane.
- **OS capability probe**: checks raw-socket capability at startup; records `passive_discovery_mode` (full | degraded) for heartbeat reporting.

## Dependencies

- COMP-001 Agent (host process, submission queue, API key)
- COMP-003 Server Ingest (via Agent submission path)

## Integration Test Boundaries

- **Listener → ingest (API call, in-process emit)**: feed a synthetic mDNS advertisement; assert a discovered device is created with the correct fingerprints and `management_status=discovered`.
- **Graceful degradation (local fault injection)**: start without raw-socket capability; assert ARP watcher falls back to snapshot mode, emits a warning, and the agent stays up. Assert heartbeat reports `passive_discovery_mode=degraded`.
- **Event-batch dedup (API call)**: emit the same SSDP device twice within a cycle; assert the source-keyed delta tracker suppresses the duplicate.
