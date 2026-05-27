# Observation Sources

Every source of information about the network implements an adapter that converts its native payload into `[]Observation` envelopes. This document catalogs all sources: what they observe, what observations they produce, and their priority assignments.

## Source Summary

| Source | Trigger | Frequency | Observations per invocation | Priority range |
|--------|---------|-----------|----------------------------|----------------|
| Agent Discovery | Agent scan cycle | Every 5 minutes | 10–200 (one per neighbor) | 15–90 (varies by method) |
| Agent Inventory | Agent self-report | Every 24 hours | 5–50 (NICs + containers + disks) | 95–100 |
| Agent Metrics | Agent heartbeat cycle | Every 30 seconds | Time-series (not observations) | — |
| Terrain DHCP | Server-side poller | Every 60 seconds | 10–100 (one per active lease) | 10 |
| Terrain DNS | Server-side poller | Every 60 seconds | 0 (feeds Derive stage, not observations) | — |
| SNMP Poller | Server-side poller | Configurable per target (60–600s) | Varies (ARP + sysName + iface counters) | 45–65 |
| Nmap Scanner | Server-side scheduled or on-demand | Configurable per target | Varies | 20–40 |
| User Input | Admin UI action | On demand | 1 | 93 |

**Source categories:**

1. **Agent-driven sources** push data to the server (Agent Discovery, Agent Inventory, Agent Metrics). Trigger is on the agent side; the server only validates and ingests.
2. **Server-side polled sources** pull from a target on a schedule. Cover Terrain DHCP, Terrain DNS, SNMP, Nmap, and any future remote-pull adapter. See [Server-Side Polled Source](#server-side-polled-source) for the shared contract.
3. **User-input sources** are one-shot operator actions through the UI.

Every source is represented by a `Source` row in the database (see entity-model.md → Source). Adapters convert source-native payloads into `[]Observation` envelopes that flow into the pipeline.


---

## Agent Discovery

**What it is:** Each agent periodically scans its local network using multiple protocols to discover neighbor devices. A single scan cycle uses 15+ different methods in parallel, each producing different quality data.

**Trigger:** Agent runs a discovery cycle every 5 minutes (configurable). Results are batched and POSTed to `POST /api/v1/agent/discoveries`.

**Wire format:** `proto.DiscoveryRequest` containing `[]proto.Sighting` + optional `NetworkContext`.

### Discovery Methods

Each method the agent uses has a distinct priority, data quality, and overlap profile:

#### Phase 0: Priming (no direct observations)

| Method | Technique | Purpose |
|--------|-----------|---------|
| ICMP multicast | Ping 224.0.0.1 | Forces kernel ARP table learning |
| DHCP lease query | Read local DHCP server leases | Seed ARP table with known clients |
| Gateway SNMP ARP | SNMP `ipNetToMediaPhysAddress` on default gateway | Discover devices on other VLANs |

#### Phase 1: Broadcast/Multicast Discovery

| Method | Priority | What it reports | Overlap with |
|--------|----------|----------------|--------------|
| `arp` | — (base) | MAC + IP existence (presence proof) | Everything uses this as the base layer |
| `mdns` | 90 | Hostname, services, TXT records | Terrain DNS (different vantage) |
| `ssdp` | 50 | UPnP device type, friendly name, model | — |
| `wsd` | 52 | WS-Discovery friendly name | SMB (same Windows devices) |

#### Phase 2: Unicast Probes

| Method | Priority | What it reports | Overlap with |
|--------|----------|----------------|--------------|
| `dhcp` | 93 | Client-announced hostname from local DHCP server | **Terrain DHCP** — same DHCP server, same leases |
| `nbns` | 70 | NetBIOS name | SMB, LLMNR (same Windows hosts) |
| `snmp` | 65 | sysName from SNMP-capable devices | — |
| `llmnr` | 85 | Link-Local Multicast Name Resolution | NBNS, SMB (Windows overlap) |
| `smb` | 80 | SMB/CIFS machine name | NBNS, LLMNR (Windows overlap) |
| `ldap` | 68 | dnsHostName from rootDSE (Active Directory DCs) | — |
| `eureka` | 62 | Google Cast/Nest device name + model | mDNS TXT `fn` key (same info, different source) |
| `ipp` | 60 | Printer name from IPP protocol | mDNS `_ipp._tcp` services |
| `roku` | 58 | Roku device name + model via ECP API | SSDP (same device advertises both) |
| `airplay` | 55 | AirPlay device name | mDNS `_airplay._tcp` (same info) |
| `garp` | 45 | Gateway ARP table entries (IPs on remote VLANs) | — |
| `tls` | 40 | TLS certificate CN/SANs | HTTP (overlapping identity from same port) |
| `rdns` | 30 | Reverse DNS PTR record | Terrain DNS (different lookup path, same data) |
| `http` | 20 | HTTP response `<title>` tag | TLS (same device, different signal) |
| `ssh` | 15 | SSH server banner + host key fingerprint | — |

#### Phase 3: Post-processing (computed, not observed)

| Step | What happens |
|------|-------------|
| OUI lookup | MAC → vendor string |
| Docker cache | MAC/IP → container name (from local Docker socket) |
| Hostname selection | Best-priority name across all methods → `sighting.Hostname` |

### Source Overlaps

Several agent methods produce information that overlaps with other sources:

| Agent method | Overlaps with | Who wins | Why |
|--------------|--------------|----------|-----|
| `dhcp` (local lease read) | Terrain DHCP (remote API query) | Agent (93 vs 10) | Agent reads leases directly from the authoritative server |
| `mdns` (local multicast) | Terrain DNS (query stats) | Agent (90) | Terrain DNS doesn't produce per-device observations |
| `rdns` (agent does PTR lookup) | Terrain DNS (top-client stats) | Agent (30) | Different data: PTR → hostname; terrain → query volume |
| `nbns` + `smb` + `llmnr` | Each other | LLMNR (85) > SMB (80) > NBNS (70) | All resolve the same Windows hostname; priority picks the freshest |
| `eureka` (Cast API probe) | mDNS TXT `fn` key | DHCP (93) > mDNS (90) > eureka (62) | Multiple paths to the same device name |

### What "Agent Discovery" is NOT

- **Not a single source** — it's a collection of 15+ methods, each with distinct priority.
- **Not authoritative about itself** — discovery sees *neighbors*, not the agent's own system. Self-knowledge comes from Agent Inventory (priority 100).
- **Not globally unique** — multiple agents can discover the same device from different vantage points. The pipeline deduplicates by `(interface_id, source_id, method)`.

### Adapter Output

Each `proto.Sighting` becomes one `Observation`. The `method` field determines priority:

```
Observation {
    SourceType: "agent-discovery"
    SourceID:   req.AgentID
    ObservedAt: sighting.SeenAt
    Method:     sighting.Method        // arp, mdns, nbns, ...
    Priority:   priorityFor(method)    // from table above
    MAC:        sighting.MAC
    IP:         sighting.IP
    Hostname:   bestHostname(sighting.HostnameSources)
    Vendor:     OUI(sighting.MAC)

    // All names observed this cycle, keyed by method
    HostnameSources: sighting.HostnameSources

    // Services (from mDNS + probes)
    Services: [{
        Name: "AdGuard Home"
        Type: "dns"
        Port: 3000
        DiscoveredVia: "mdns"
    }, ...]

    // Extras: full probe payloads
    Extras: {
        "txt": sighting.TXT,
        "eureka": sighting.Probes["eureka"],
        "ipp": sighting.Probes["ipp"],
        "roku": sighting.Probes["roku"],
        "airplay": sighting.Probes["airplay"],
        "ldap": sighting.Probes["ldap"],
        "ssh_fp": sighting.Probes["ssh_fp"],
        "dhcp": sighting.Probes["dhcp"],
    }
}
```

### Network Context

When the agent provides `NetworkContext` (gateway MAC, CIDR, SSID), the pipeline also produces a **network observation** — triggering network entity creation/update in the Identify stage.

### Grouping Evidence

Discovery generates **correlation evidence** for the Identify stage:
- Same mDNS hostname from multiple MACs → likely same hardware
- Same SSH host key from multiple IPs → same system
- Container/VM sightings on bridge interfaces → children of reporting agent's host

---

## Agent Inventory

**What it is:** Each agent reports its own full system state — hardware, OS, NICs, disks, Docker containers, processes, routes, etc.

**Trigger:** Every 24 hours (or on significant state change). POSTed to `POST /api/v1/agent/inventory`.

**Wire format:** `proto.InventoryRequest` containing `proto.Inventory`.

### Adapter Output

One inventory report produces multiple observations:

**Per NIC (agent's own interfaces):**
```
Observation {
    SourceType: "agent-inventory"
    SourceID:   req.AgentID
    Method:     "inventory"
    Priority:   100                     // highest: agent knows itself
    MAC:        iface.MAC
    IP:         iface.IPv4[0]           // primary IP on this NIC
    Hostname:   agent.Hostname

    System: {
        AgentID:   req.AgentID
        Hostname:  agent.Hostname
        OSFamily:  inventory.OS.Family
        OSDistro:  inventory.OS.Distro
        OSVersion: inventory.OS.Version
        Type:      "bare-metal"
        Lifecycle: "persistent"
        State:     "running"
    }

    Hardware: {
        Serial:     inventory.Hardware.SystemSerial
        Vendor:     inventory.Hardware.SystemVendor
        Model:      inventory.Hardware.SystemModel
        FormFactor: inferFormFactor(inventory)
    }

    Disks: [{
        DevicePath: disk.Device
        Serial:     disk.Serial
        Model:      disk.Model
        SizeBytes:  disk.SizeBytes
        Type:       disk.Type
        SMARTHealth: disk.SMART.Health
        SMARTJSON:   disk.SMART.Attributes
    }, ...]
}
```

**Per Docker container:**
```
Observation {
    SourceType: "agent-inventory"
    SourceID:   req.AgentID
    Method:     "inventory"
    Priority:   95                       // docker runtime is authoritative
    MAC:        container.Networks[i].MAC
    IP:         container.Networks[i].IPv4
    Hostname:   container.Name

    System: {
        AgentID:      ""                 // container doesn't run our agent
        ContainerID:  container.ID
        HostAgentID:  req.AgentID        // which agent hosts it
        Hostname:     container.Name
        Type:         "container"
        Lifecycle:    "ephemeral"
        State:        container.State    // running, stopped, etc.
    }

    Services: [{
        Name: container.Name
        Type: inferServiceType(container.Image)
    }]

    Extras: {
        "image":           container.Image,
        "compose_project": container.ComposeProject,
        "compose_service": container.ComposeService,
        "labels":          container.Labels,
    }
}
```

### Container Lifecycle Sync

The inventory adapter also produces **absence signals** — containers that were previously reported by this agent but are missing from the current report. These trigger `state = removed` in the Derive stage.

### Disk Reporting

Disks are only reported via inventory (they're local to the agent's system). SMART data, temperatures, and partition info are all carried in `DiskObservation`.

---

## Server-Side Polled Source

A base category — not a single adapter. Every adapter that *pulls* from a remote target on a schedule fits this contract: Terrain DHCP, Terrain DNS, SNMP, Nmap, and any future remote-pull adapter.

### Shared Contract

Every server-side polled source MUST:

1. **Be persisted as a `Source` row** (see entity-model.md → Source). Operators add/remove/disable sources through the UI; nothing about a polled source is hardcoded except its adapter kind.
2. **Declare a `kind`** (`terrain-dhcp` / `terrain-dns` / `snmp-poller` / `nmap-scanner` / ...). The adapter for that kind reads the `Source.config_json` and runs.
3. **Honor `Source.poll_interval_seconds`** as authoritative. The scheduler reads this from the row, not from code constants.
4. **Update `last_success_at` / `last_error_at` / `last_error_message` / `consecutive_error_count`** on every poll attempt. The UI surfaces these directly on the Sources page.
5. **Back off on consecutive errors.** After N (default 5) consecutive failures the scheduler doubles the effective interval, capped at 1 hour. A successful poll resets the counter and the interval.
6. **Respect `enabled`.** A disabled source is skipped without error — it does not count as failure and does not advance `last_*_at`.
7. **Emit one `Observation` per atomic finding** from the polled snapshot. The scheduler hands the observations to the pipeline like any other adapter output; there are no source-specific code paths in Identify/Merge/Derive/Store.
8. **Be idempotent.** The same target polled twice produces equivalent observations. Pipeline bucketing deduplicates.

### Configuration Storage

All polled-source configuration lives in the `sources` table — there is no file-based source config. Operators create, edit, and disable sources from the admin UI; the scheduler picks up changes on its next poll cycle without a server restart.

The `config_json` field is a per-kind schema (documented below for each adapter). Field names are stable across versions; adapters validate on read and refuse to start with a clear error if a required field is missing or malformed.

### Credential Handling

Sources that need credentials (SNMP communities, terrain server passwords, authenticated API tokens) store the secret **inside `config_json`** in a designated field (by convention named `password`, `token`, `community`, etc.). These fields are encrypted at rest with the server's data encryption key — see infrastructure.md → Secret Storage for the mechanics.

Write path: the UI submits the cleartext secret over the authenticated HTTPS admin endpoint; the server encrypts before writing the row.

Read path: only the adapter that consumes the source decrypts. API responses that return `config_json` to the UI replace secret fields with the sentinel `"<set>"` so secrets never round-trip back to the browser. Updating a secret uses a dedicated write-only endpoint (`PATCH /api/v1/sources/{id}/secrets`).

Rotating a credential is a UI action — edit the source, submit the new value, the next poll uses it. No keyring file, no restart.

### Source ↔ Service Linkage

When a polled source's target is also a monitored Service on a managed host (e.g., the AdGuard daemon is both a Source we poll AND a Service we want to alert on if it stops), the operator can link them by setting `Source.service_id`. This is optional — many polled sources have no corresponding Service (Nmap against an IP range, SNMP against a switch we don't otherwise monitor).

The Source ↔ Service link is one-way: the Source row knows its Service; the Service row doesn't carry source state. This factoring lets Service health checks and Source poll state evolve independently.

---

## Terrain DHCP

**What it is:** A [Server-Side Polled Source](#server-side-polled-source) of kind `terrain-dhcp` that queries a LAN DNS/DHCP server (AdGuard Home, Technitium DNS, or Pi-hole) and extracts DHCP lease information.

**Source `config_json`:**
```json
{
  "base_url": "https://adguard.lan",
  "username": "admin",
  "password": "<encrypted at rest>",
  "flavor": "adguard"  // or "technitium", "pihole"
}
```

The `password` field is encrypted before insert (see [Credential Handling](#credential-handling)). API reads return `"password": "<set>"`.

**Trigger:** `Source.poll_interval_seconds` (default 60). Server-side, no agent involvement.

**Wire format:** `[]terrain.DHCPLease` from the poller's internal state.

### Adapter Output

Each active DHCP lease becomes one observation:

```
Observation {
    SourceType: "terrain-dhcp"
    SourceID:   "terrain"               // or terrain server URL
    ObservedAt: pollTimestamp
    Method:     "dhcp"
    Priority:   10                       // lowest: passive, possibly stale
    MAC:        lease.MAC
    IP:         lease.IP
    Hostname:   lease.Hostname           // client-announced name
    Vendor:     OUI(lease.MAC)

    Extras: {
        "static": lease.Static,          // pinned reservation?
        "expires": lease.Expires,
    }
}
```

### Key Behaviors

- **Low priority (10):** Terrain observations never overwrite data from richer sources. They only populate fields that are currently empty.
- **No `last_seen_at` advancement:** A DHCP lease existing doesn't mean the device is currently on the network. The pipeline doesn't treat terrain observations as proof of device liveness.
- **No `seen_by_agent` claim:** Terrain is not an agent. It doesn't claim to have "seen" the device.
- **Static lease flag:** Stored as a derived property on the interface (is this MAC pinned in DHCP?).
- **Expired lease filtering:** The terrain poller already filters out expired non-static leases before handing to the adapter.

---

## Terrain DNS

**What it is:** A [Server-Side Polled Source](#server-side-polled-source) of kind `terrain-dns`. DNS query statistics from the upstream DNS server (total queries, blocked queries, top clients, top domains).

**Trigger:** Same poller infrastructure as Terrain DHCP, separate `Source` row with its own poll interval.

**Wire format:** `terrain.DNSStats` from the poller.

### NOT an observation source

DNS stats don't describe individual devices — they're aggregate network telemetry. They feed the **Derive stage** rather than producing observations:

- `TopClients` → cross-referenced with interface IPs to compute per-device DNS activity
- `TotalQueries` / `BlockedQueries` → network-level health metrics
- `TopBlocked` → informational for the dashboard

This data is stored as network-level or service-level metadata, not as interface observations.

---

## User Input

**What it is:** Admin manually provides information about a device through the UI.

**Trigger:** Form submission on device detail page.

**Wire format:** HTTP form fields (hostname override, notes, tags, manual grouping).

### Adapter Output

```
Observation {
    SourceType: "user"
    SourceID:   "admin:" + username
    ObservedAt: time.Now()
    Method:     "manual"
    Priority:   93                       // high: user explicitly set it
    MAC:        device.MAC
    Hostname:   formValue("hostname")    // only if user provided override

    Extras: {
        "notes": formValue("notes"),
        "tags":  formValue("tags"),
    }
}
```

### Special Cases

- **Manual hardware grouping:** User drags interfaces together in UI → produces a hardware merge operation (not a standard observation). Sets `identity_source = user`, locks the grouping.
- **Manual hardware split:** User separates incorrectly grouped interfaces → produces a hardware split operation.
- **Notes and tags:** These aren't priority-competitive fields — they're additive. Always applied regardless of source.

---

## SNMP Poller

**Status:** Approved future work. Architecturally committed as a [Server-Side Polled Source](#server-side-polled-source). The Source entity model already accommodates SNMP target configuration; no further schema work is required to add it.

**What it is:** Server polls SNMP-capable devices (routers, managed switches, printers, NAS appliances) for ARP tables, interface counters, and system identity.

**Source kind:** `snmp-poller`.

**Source `config_json`:**
```json
{
  "target_host": "192.168.1.1",
  "snmp_version": "v2c",
  "community": "<encrypted at rest>",
  "oid_profile": "router"  // canned OID set: "router" | "switch" | "printer" | "host"
}
```

For SNMPv3:
```json
{
  "target_host": "10.0.0.5",
  "snmp_version": "v3",
  "username": "monitor",
  "auth_protocol": "SHA",
  "auth_password": "<encrypted at rest>",
  "priv_protocol": "AES",
  "priv_password": "<encrypted at rest>",
  "oid_profile": "switch"
}
```

Secret fields (`community`, `auth_password`, `priv_password`) are encrypted on write and returned as `"<set>"` on read — see [Credential Handling](#credential-handling).

**Observations produced:**
- One per ARP entry on the device — `method=garp`, priority 45 (matches existing gateway SNMP scraping that runs from agents today).
- System identity from sysName/sysDescr — `method=snmp`, priority 65.
- Interface counters as **metrics**, not observations. They land in `interface_snapshots` with a synthetic `agent_id` representing the SNMP source (or against the linked Service if `Source.service_id` is set).

**Requirements before implementation:**
- Encrypted-secret storage in `config_json` (covered by [Credential Handling](#credential-handling) — no separate work).
- A small OID library covering the four profiles above. Per-vendor MIB ingestion is explicitly out of scope.

**Why now:** the original architecture left SNMP "future / speculative." With the polled-source contract formalized, SNMP becomes one more adapter — no architectural questions remain. Implementation can be scheduled when there's a user-visible reason to prioritize it.

---

## Nmap Scanner

**Status:** Approved future work. Same architectural pattern as SNMP — a [Server-Side Polled Source](#server-side-polled-source) of kind `nmap-scanner`.

**What it is:** On-demand or scheduled port scanning of specific targets or subnets, used to discover devices we don't otherwise see (no agent, no mDNS, no DHCP visibility).

**Source `config_json`:**
```json
{
  "target_range": "192.168.50.0/24",
  "scan_profile": "fast-tcp",  // "fast-tcp" | "full-tcp" | "udp-common" | "version-detect"
  "rate_limit_pps": 200,
  "max_concurrent_targets": 16
}
```

**Observations produced:**
- Open ports → ServiceObservation entries (method=`nmap-port`, priority 20).
- OS fingerprint → SystemObservation (method=`nmap-os`, priority 35) — below active probes, above purely passive sources, because fingerprinting is an inference not a direct signal.
- Banner grabs → carried in the Observation `Extras` until promoted (see `data-pipeline.md` → Extras Promotion Rule).

**Requirements before implementation:**
- Operator must explicitly create the Nmap Source (no auto-scanning of discovered networks).
- Rate limit enforcement at the adapter, not in nmap config — we don't trust the upstream tool to throttle itself.
- Each scan run is bounded in wall-clock time; runaway scans are killed and surface as `last_error_message="timeout"`.

**Why now:** same as SNMP. The polled-source contract handles the scheduling, credentials (none here), back-off, and error surfacing. Nmap-specific logic is confined to the adapter.

---

## Adapter Registration

Adapters are registered at server startup, keyed by `Source.kind`. They are an implementation detail of the **Ingestor** (see `data-pipeline.md` → Pipeline Invocation) — HTTP handlers and the polled-source scheduler call `Ingestor.Ingest(ctx, kind, payload)` and never touch adapters directly.

```go
// Server startup — register one adapter per kind with the Ingestor.
ingestor := pipeline.NewIngestor(pipe, store, map[string]Adapter{
    "agent-discovery": &adapters.AgentDiscovery{},
    "agent-inventory": &adapters.AgentInventory{},
    "terrain-dhcp":    &adapters.TerrainDHCP{},
    "terrain-dns":     &adapters.TerrainDNS{},
    "snmp-poller":     &adapters.SNMPPoller{},     // future
    "nmap-scanner":    &adapters.NmapScanner{},    // future
    "user":            &adapters.UserInput{},
})

// Scheduler loop (server-side polled sources)
for _, src := range store.ListEnabledSources() {
    if !due(src) { continue }
    // Ingestor wraps adapter.Poll(src) + pipeline.Process(observations)
    // and records poll success/failure on the source row.
    _, _ = ingestor.PollSource(ctx, src)
}

// Push-driven sources (agent endpoints) call the same entry point from the HTTP handler:
//   result, err := ingestor.Ingest(ctx, "agent-discovery", req)
```

The scheduler is the only code that knows about `Source` rows for polled sources. Adapters are stateless: they take a Source + context, return observations. Push-driven sources (agent endpoints) never touch the scheduler; the HTTP handler hands the payload to the Ingestor, which dispatches to the right adapter and runs the pipeline.

---

## Adding a New Source

To add a new push-driven source:

1. Define the adapter implementing the `Adapter` interface.
2. Map source-specific fields to `Observation` envelope fields.
3. Assign appropriate priority from the global table (or propose a new entry if the source doesn't fit existing categories).
4. Register the adapter under a new `kind` at startup.
5. Wire the trigger (HTTP handler for push, or just rely on the polled-source scheduler).

To add a new polled source:

1. Same steps 1–4 above.
2. Add a UI form for operators to create `Source` rows of that kind (kind dropdown, `config_json` schema, credential picker).
3. Document the `config_json` schema in this file under a new section modeled on Terrain DHCP / SNMP Poller above.

**That's it.** No new SQL, no merge logic, no special-case entity resolution. The pipeline handles everything downstream of the adapter; the scheduler handles polling lifecycle uniformly.
