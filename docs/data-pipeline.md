# Data Pipeline

Every observation flows through five sequential stages. No source bypasses stages. No stage has source-specific logic — the adapter (Stage 1) is the only place that knows about source-specific wire formats.

```
┌──────────┐    ┌──────────┐    ┌──────────┐    ┌──────────┐    ┌──────────┐
│  Ingest  │───▶│ Identify │───▶│  Merge   │───▶│  Derive  │───▶│  Store   │
└──────────┘    └──────────┘    └──────────┘    └──────────┘    └──────────┘
     │               │               │               │               │
  Normalize      Resolve to       Apply to        Compute          Write
  raw input      existing or      resolved        derived          final
  into           new entities     entities        state            state
  Observations                    (priority-                       to DB
                                  aware)
```

## Stage 1: Ingest

**Input:** Source-specific payload (proto.DiscoveryRequest, proto.InventoryRequest, terrain.DHCPLease, etc.)

**Output:** `[]Observation` — normalized, validated, enriched with OUI vendor lookup.

**Responsibilities:**
1. Validate input (reject malformed MAC, empty required fields).
2. Normalize: MAC lowercased, IP validated, hostname trimmed, timestamps to UTC.
3. OUI vendor lookup from MAC prefix.
4. Convert source-specific structure into one or more `Observation` envelopes.
5. Assign `priority` based on source type + method (from the global priority table).
6. **Resolve short keys to entity IDs.** Per-cycle metric snapshots and discovery deltas carry short stable keys (`device_name`, `iface_name`) rather than full identity. The ingest stage joins these against `(agent_id, key)` to recover the entity ID. If no match exists, the snapshot is held and the response carries an `If-Inventory-Stale` hint so the agent re-ships inventory on the next cycle.
7. **Decompress and verify content hashes.** Gzip-decompress the request body. For sub-payloads with a declared content hash, verify against the server's last-known hash; if the agent declared "skip — unchanged" but the server has no record, return a "resend" response so the agent ships the body on the next cycle.

**Rules:**
- No database access *for transformation*. Hash and key lookups are read-only DB queries scoped to the ingest path.
- Each source has exactly one adapter implementing the `Adapter` interface.
- One source payload may produce many observations (e.g., a DiscoveryRequest with 50 sightings → 50 observations).
- Adapter must never panic on malformed input — skip bad records, log warnings.

### Adapter Interface

```go
type Adapter interface {
    Source() string
    Adapt(ctx context.Context, payload any) ([]Observation, error)
}
```

### Observation Envelope

```go
type Observation struct {
    // Source identification
    SourceType string    // adapter name: "agent-discovery", "terrain-dhcp", etc.
    SourceID   string    // specific instance: agent UUID, poller name
    ObservedAt time.Time // when the source made this observation
    Method     string    // protocol: arp, mdns, dhcp, inventory, ...
    Priority   int       // from global priority table

    // Interface-level (minimum: MAC required)
    MAC      string
    IP       string
    Hostname string
    Vendor   string // OUI result

    // System-level (optional — richer sources populate this)
    System *SystemObservation

    // Hardware-level (optional — only from agent inventory)
    Hardware *HardwareObservation

    // Service-level (optional — probe results, mDNS service types)
    Services []ServiceObservation

    // Disk-level (optional — only from agent inventory)
    Disks []DiskObservation

    // Source-specific extras (TXT records, DHCP fields, probe payloads)
    Extras map[string]any
}
```

---

## Stage 2: Identify

**Input:** One `Observation` at a time.

**Output:** A resolution result: which existing entities this observation maps to, or flags indicating new entities should be created.

```go
type Resolution struct {
    HardwareID    string // existing or "" (create new)
    SystemID      string // existing or "" (create new)
    InterfaceID   string // always the MAC (exists or will be inserted)
    IsNewHardware bool
    IsNewSystem   bool
    IsNewInterface bool
}
```

**Responsibilities:**

### Interface resolution (always succeeds)
1. Lookup by MAC in interfaces table.
2. If found → use existing interface_id (the MAC).
3. If not found → flag `IsNewInterface = true`. Will be created in Store stage.

### System resolution
Decision tree:
```
observation.System.AgentID != "" ?
  → System with agent_id = X exists? → use it
  → else → IsNewSystem (type=bare-metal, lifecycle=persistent)

observation.System.ContainerID != "" ?
  → System with container_id = X on host = Y exists? → use it
  → else → IsNewSystem (type=container, lifecycle=ephemeral)

Interface already has system_id ?
  → use that system

else → IsNewSystem (type inferred from context, lifecycle=persistent)
```

### Hardware resolution
Decision tree:
```
System already has hardware_id ?
  → use that hardware

observation.Hardware.Serial != "" AND existing hardware with that serial?
  → use it (and link system if not already linked)

observation.System.AgentID != "" ?
  → Hardware entity tagged with this agent exists? → use it
  → else → IsNewHardware (identity_source=agent, confidence=high)

else → run correlation engine (separate subsection below)
```

### Correlation Engine

For unmanaged devices where no agent or serial definitively identifies hardware:

1. **SSH host key match:** If observation includes SSH host key fingerprint, search all interfaces with same key → same hardware. Confidence: high.
2. **mDNS hostname match:** If observation hostname (via mDNS) matches another interface's mDNS hostname → likely same hardware. Confidence: medium.
3. **NBNS/LLMNR name match:** Same as mDNS but lower confidence. Confidence: medium.
4. **rDNS match across IPs:** Same PTR name for different IPs with different MACs. Confidence: low (could be load balancer).
5. **No match:** Create new hardware entity 1:1 with the interface. Confidence: low.

Correlation results update `identity_evidence` on the hardware entity, appending the new evidence and recomputing confidence.

**Admin override:** If a user has manually merged or split hardware entities, those decisions are locked (marked `identity_source = user`) and the correlation engine won't touch them.

---

## Stage 3: Merge

**Input:** Observation + Resolution (which entities to update).

**Output:** Field-level update instructions for each entity.

**Responsibilities:**
- Apply priority-aware field updates.
- Record observation in history.
- Update timestamps.
- Never discard data — losing values go to observation history, not oblivion.

### Global Priority Table

Every observation carries a priority derived from its source + method. This is the single canonical table — the codebase (`discover.sourcePriority` and `store.HostnameSourcePriority`) must match these values exactly.

| Source/Method | Priority | Rationale |
|--------------|----------|-----------|
| agent (self-report/inventory) | 100 | Definitive for its own system |
| docker (container runtime) | 95 | Authoritative for containers |
| user (manual input) | 93 | Human explicitly set it |
| dhcp (client announcement) | 93 | Client tells DHCP server its own name |
| mdns | 90 | Actively advertised by device |
| llmnr | 85 | Actively advertised |
| smb | 80 | Actively advertised |
| nbns | 70 | Actively advertised |
| ldap | 68 | AD domain controller identity (rootDSE) |
| snmp | 65 | Queried from device sysName |
| eureka | 62 | Google Cast self-report |
| ipp | 60 | Printer self-report |
| roku | 58 | Roku self-report via ECP |
| airplay | 55 | AirPlay self-report |
| wsd | 52 | WS-Discovery friendly name |
| ssdp | 50 | UPnP advertisement |
| garp | 45 | Gateway ARP table scrape via SNMP |
| tls | 40 | Certificate CN/SAN |
| rdns | 30 | PTR record (often stale) |
| http | 20 | Banner/title scraping |
| ssh | 15 | SSH version string / host key |
| terrain-dhcp | 10 | Passive observation from upstream DNS/DHCP API |

### Merge Rules

Applied uniformly to all single-value fields on all entity types:

```
function merge_field(stored_value, stored_priority, incoming_value, incoming_priority):
    if incoming_value is empty:
        return stored_value, stored_priority  // never blank out data
    if incoming_priority >= stored_priority:
        return incoming_value, incoming_priority  // higher or equal priority wins
    else:
        return stored_value, stored_priority  // keep existing
```

**Timestamp fields:**
```
first_seen_at = MIN(stored, incoming.observed_at)
last_seen_at  = MAX(stored, incoming.observed_at)
```

**last_seen_by:**
```
Only update when last_seen_at actually advances (incoming > stored).
Source must be a real agent, not a passive poller.
```

**Accumulating fields (labels, identity_evidence):**
```
Deep merge: add new keys, update existing keys if priority allows, never remove keys.
```

**Service entities:**
```
Services reported by observations are upserted as Service entities (keyed by system_id + port + protocol).
If a service already exists, update its fields by the same priority rules above.
Services are never auto-deleted — only marked stale when absent from a managed agent's inventory.
```

**Interface addresses:**
```
When an observation reports an IP for a MAC:
  1. Upsert into interface_addresses (key: interface_id + address).
  2. Set source, source_id, last_seen_at.
  3. Recompute is_primary for this interface+family pair.
No priority competition for addresses — all observed addresses are recorded.
Addresses don't overwrite each other; an interface legitimately has multiple.
Most-recently-seen address per family is flagged is_primary for display.
```

**Address reassignment (IP moves between MACs):**
```
When observation reports IP=X on MAC=B, but interface_addresses already has IP=X on MAC=A:
  - If observation is from a real-time source (agent-discovery, agent-inventory):
    → Mark the old row (MAC=A, IP=X) as stale (last_seen_at frozen, is_primary=false).
    → Create/update the new row (MAC=B, IP=X) with current timestamp.
  - If observation is from a passive source (terrain-dhcp):
    → Only update if observation timestamp > old row's last_seen_at.
This handles DHCP reassignment: device A releases .50, device B gets .50.
The old device's address becomes stale; the new device's address is current.
```

**Hostname aliases:**
```
Every (hostname, method) pair reported in an observation is recorded in the hostname_aliases table.
This is append-only (bucketed by interface_id + name + source): increment count, update last_seen_at.
The canonical hostname on the System entity is determined by the priority table.
```

### Multi-Hostname Observations

A single discovery sighting can carry multiple hostnames — one per discovery method. For example, one scan of device X might produce:

```
HostnameSources: {
    "mdns":   "livingroom-speaker.local",
    "nbns":   "LIVINGROOM",
    "ssdp":   "Sonos One",
    "eureka": "Living Room Speaker",
}
```

**How this flows through the pipeline:**

1. **Ingest:** The adapter produces ONE observation per sighting (per MAC), but sets `Observation.Hostname` to the best-priority name (for the canonical field merge) and carries the full `HostnameSources` map in `Extras`.

2. **Merge — canonical hostname:** The best-priority name from `Observation.Hostname` competes with the stored canonical hostname using standard priority rules. If mdns (priority 90) reported "livingroom-speaker.local" and the stored hostname came from rdns (priority 30), mdns wins.

3. **Merge — hostname aliases:** For EACH entry in `HostnameSources`, the pipeline records a hostname alias row: `(interface_id, name, source)`. This means one observation can produce 4+ alias upserts. Each alias is bucketed independently — they're separate observed facts.

4. **Result:** The System's canonical `hostname` field shows the single best name. The hostname_aliases table preserves *all* names from *all* sources, letting the UI show conflicts and alternatives.

**Priority collision within one sighting:** If two methods in the same sighting have the same priority (unlikely given the current table, but possible with future sources), the tie is broken by the method ordering in `sourcesByPriority()` — whichever appears first wins.

### Multi-Agent Observations

Multiple agents can discover the same device from different network vantage points. This is expected behavior (e.g., two agents on the same subnet both see the same printer via ARP).

**How duplicates are handled:**

1. **Observation layer:** Each (interface_id, source_id, method, ip) tuple is a separate bucketed row. Agent A seeing MAC X via ARP and Agent B seeing MAC X via ARP are two distinct observation rows with different source_id values.

2. **Entity layer:** The Interface entity is singular (keyed by MAC). Both agents' observations resolve to the same interface. The merge rules apply normally — last_seen_at advances if either agent's timestamp is newer.

3. **last_seen_by:** Tracks the most recent *active* observer. If Agent A sees the device at T=5 and Agent B sees it at T=7, last_seen_by = Agent B. This is purely informational — it doesn't affect priority.

4. **Hostname competition:** If Agent A reports hostname "printer" via mDNS and Agent B reports "HP-LaserJet" via mDNS, the most recent observation wins (same priority → timestamp tiebreak). Both are recorded as aliases.

5. **No deduplication at ingest:** The pipeline deliberately does NOT deduplicate across agents. Each agent's observations are processed independently. The entity model naturally converges because entities are keyed by MAC, not by source.

---

## Stage 4: Derive

**Input:** The updated entity state after Merge.

**Output:** Derived/computed fields that depend on cross-entity relationships or business rules.

**Runs after every merge operation.** Some derivations are cheap (run inline), others are expensive (run on a schedule).

### Inline Derivations (run per-observation)

1. **Network membership** — After address upsert, check which network's CIDR contains the address. Update interface_networks junction (interface_id, network_id, via_address). Handle IP reassignment: if an address moved from interface A to interface B, remove A's membership in that network (if it has no other addresses in the CIDR).

2. **Interface type classification** — Based on interface name patterns, MAC OUI, and context (is it reported by agent as a bridge? Is the MAC locally-administered?).

3. **System state update** — If observation proves system is alive → state = running. For containers: if host reports inventory without this container → state = removed.

4. **Discovery profile materialization** — When an observation carries rich probe data (mDNS TXT records, Eureka/IPP/Roku/AirPlay identity, SSH host keys, LDAP rootDSE, TLS SANs), extract and write into the structured profile tables on the Interface entity: `interface_profile` (1:1 scalar fields), `interface_mdns_services`, `interface_mdns_txt`, `interface_tls_sans`. See entity-model.md → Interface for the table schemas. This replaces the deprecated `latest_profile_json` blob — every value is now a queryable column.

### Scheduled Derivations (run periodically)

5. **Kind classification** — Determine hardware/system "kind" from aggregated evidence:
   - Has Docker containers → likely server
   - Eureka probe matched → media device
   - IPP probe matched → printer
   - LDAP rootDSE → domain controller
   - Multiple NICs + server OS → server
   - Rules engine, not hardcoded per-source.

6. **DNS activity cross-reference** — Match interface IPs against terrain's TopClients list. Store as derived metadata, not raw observation.

7. **Offline detection** — Systems whose last_seen_at is older than threshold → state = offline.

8. **Hardware confidence recomputation** — When new correlation evidence arrives for a hardware group, recompute overall confidence.

9. **Stale entity cleanup** — Ephemeral systems past retention → DELETE. Stale observations past retention → DELETE (if retention policy configured).

10. **Stale address cleanup** — Interface addresses whose last_seen_at is older than a configurable threshold AND not from a managed agent → marked stale. Addresses from managed agents are only staled when the agent explicitly reports the interface without that address.

---

## Stage 5: Store

**Input:** All updates from Merge + Derive stages.

**Output:** Database writes in a single transaction.

**Write order (respects FK constraints):**
1. Hardware (INSERT or UPDATE)
2. System (INSERT or UPDATE)
3. Interface (INSERT or UPDATE)
4. Interface Addresses (UPSERT — handles IP reassignment)
5. Service (INSERT or UPDATE, if service observations present)
6. Disk (INSERT or UPDATE, if disk observations present)
7. Observation (INSERT with bucketing)
8. Hostname aliases (INSERT or UPDATE count)
9. Structured profile tables (interface_profile, interface_mdns_*, interface_tls_sans)
10. Host-posture extension tables (update_status, security_posture, firewall_*, antivirus_products, encrypted_volumes, failed_services, listening_ports, local_users, logged_in_sessions, routes, packages, hassio_*) — written per agent ingest
11. Container detail tables (container_metadata, container_mounts, container_ports, system_labels, docker_images, docker_volumes, docker_networks_engine)
12. Hardware extension tables (hardware_cpu, hardware_memory, hardware_board, hardware_bios, hardware_gpu)
13. Snapshot tables (metric_snapshots, disk_snapshots, interface_snapshots, filesystem_snapshots, temperature_snapshots, battery_snapshots, processes_snapshot)
14. Derived junction tables (interface_networks, system_labels, tags propagation)
15. agent_inventory_receipts (audit row, one per ingest)

**Transaction scope:** One transaction per observation batch. A batch is typically one API request (e.g., one DiscoveryRequest with N sightings = one batch).

**Idempotency:** Processing the same observation twice (exact same content) must produce the same state. Bucketing handles this — duplicate observations increment count without side effects.

---

## Shared Component: Identity Resolver

Two places in the pipeline need to translate something-short-and-stable into a full entity identity:

- **Stage 1 (Ingest)** — per-cycle metric/discovery deltas carry short keys (`device_name`, `iface_name`) and must be joined to existing Disk/Interface entity IDs before storage.
- **Stage 2 (Identify)** — incoming observations must be mapped to Hardware/System/Interface entities, creating new ones when no match exists.

Previously these were two separate code paths with overlapping logic (e.g., both knew how to map `(agent_id, device_name)` to a Disk). They are factored into ONE component — the **Identity Resolver** — exposed to both stages:

```
type IdentityResolver interface {
    // Per-cycle key resolution (Stage 1 use)
    ResolveDisk(agentID, deviceName string) (diskID string, err error)
    ResolveInterface(agentID, ifaceName string) (interfaceID string, err error)

    // Full observation resolution (Stage 2 use)
    ResolveObservation(obs Observation) (Resolution, error)
}
```

**Invariants:**

1. **One source of truth for resolution rules.** If we change how Hardware is keyed (e.g., add a new correlation heuristic), the change lives in one place and both stages pick it up.
2. **Read-only.** The Resolver never writes. New-entity flags (`IsNewHardware`, `IsNewInterface`) are returned to the caller, which decides whether to create. This keeps resolution side-effect-free and testable.
3. **No caching across batches.** Per-batch memoization is allowed (the same MAC may appear in 50 sightings within one DiscoveryRequest); cross-batch caching would create stale-ID hazards under multi-agent races.
4. **Same priority table.** The Resolver consults the same priority table the Merge stage uses for hostname tie-breaks during correlation.
5. **Fresh instance per batch.** Concurrent batches (two agent `/tick`s arriving at the same instant, or a polled source running parallel to a push ingest) each get their own `IdentityResolver` instance constructed at the start of the batch and discarded at the end. The resolver itself is not goroutine-safe; the per-batch memoization cache lives inside the instance and is therefore implicitly scoped. This is simpler than a shared resolver with a thread-safe batch-id-keyed cache and eliminates the class of bug where two batches accidentally observe each other's in-flight resolutions.

The Resolver is the only component allowed to know the full correlation decision tree. Adapters, the scheduler, and HTTP handlers must not implement ad-hoc lookups.

---

## Extras Promotion Rule

The `Observation.Extras` map is a deliberate escape hatch — adapters can ship source-specific fields (DHCP option codes, mDNS TXT key/values, SNMP raw OIDs) without the pipeline knowing about them up-front. This keeps adapter development cheap.

**The risk:** Extras become a permanent dumping ground. Operators start to depend on Extras values being present, but they're invisible to filters, alerts, and aggregations because they live in a JSON column on the Observation row.

**The rule:** After an Extras key has been **continuously produced by an adapter for 3 months** AND there is at least one user-visible feature (filter, alert, panel) that needs it, the key MUST be promoted to either:

1. A column on the Observation envelope (if it's truly per-observation transient data), OR
2. A column on the relevant structured table (e.g., `interface_profile`, a host-posture extension), OR
3. Its own per-source detail table (if it's high-cardinality multi-value data).

**Mechanics:**

- The Foundation Critic generates a quarterly report: for each `(SourceType, ExtrasKey)` pair, count weeks observed and current usage. Anything ≥12 weeks old with non-zero render usage is flagged for promotion.
- Promotion is a regular schema migration. The adapter stops writing the Extras key, the migration backfills (or marks the historical Extras values as "pre-promotion, won't backfill" depending on operator preference), and the new column becomes the source of truth.
- After promotion, the Extras key is **forbidden** from re-appearing. The Adapter SDK lints against it.

**Why a rule, not a vibe:** without an explicit promotion deadline, Extras grows monotonically. Three months is long enough that genuinely transient experiments fall off; short enough that production-load reliance can't accrete unnoticed.

---

## Alert Evaluator as a Second Writer

The pipeline is not the only thing writing to the database. The Alert Evaluator runs on its own schedule (default every 30 seconds — see metrics-and-alerting.md) and writes to two tables:

- `alert_firings` — INSERT on new alert, UPDATE `resolved_at` on resolution.
- `events` — INSERT one row per fire/resolve transition.

This is the only legitimate non-pipeline writer in the system. Its invariants are explicit:

1. **Read-only on everything else.** The evaluator queries `metric_snapshots`, `alert_rules`, `agents`, host-posture tables, etc. but writes nothing to them. The Foundation Critic verifies this with a grep over the evaluator package.
2. **Append-only on firings.** New `alert_firings` rows are inserted; existing rows are only mutated by setting `resolved_at` and `notified`. No DELETE outside the retention sweeper.
3. **Serialization via SQLite WAL.** The evaluator and the pipeline are concurrent readers and writers. They serialize through SQLite's WAL writer lock — no application-level mutex is needed, but the evaluator MUST keep its write transactions short (< 100 ms) to avoid stalling pipeline ingest.
4. **No cycles.** The evaluator does NOT call back into the pipeline. If a firing should produce an Event row, the evaluator writes the Event directly. (Events are not Observations — they describe our reactions, not the network.)
5. **Idempotent.** Evaluating the same condition twice without state change is a no-op. The evaluator carries no in-memory firing state; the source of truth is the `alert_firings` table.

This is documented here (not just in metrics-and-alerting.md) so the pipeline architecture call-out clearly names the second writer instead of pretending the pipeline is the only path to the database.

---

## Pipeline Invocation

The pipeline is invoked through the **Ingestor** — a thin facade that owns adapter dispatch and pipeline entry. HTTP handlers and the polled-source scheduler both call the Ingestor; neither talks to adapters or the pipeline directly.

```go
type Ingestor interface {
    // Ingest dispatches `payload` to the adapter registered for `kind`,
    // then feeds the produced observations through the pipeline in one batch.
    Ingest(ctx context.Context, kind string, payload any) (Result, error)
}
```

**Why a facade.** Previously HTTP handlers had two dependencies that changed together (the adapter registry and the pipeline). Adding a new source kind forced edits in every handler. With the Ingestor, handlers depend on one thing; the Ingestor owns adapter lookup, the per-batch `IdentityResolver` construction, the pipeline call, and result shaping. Adapters become a private implementation detail of the Ingestor.

The handler's responsibility shrinks to:
1. Authenticate/authorize the request.
2. Decode the request body.
3. Call `Ingestor.Ingest(ctx, kind, payload)`.
4. Return a response shaped from the `Result`.

```go
// In an HTTP handler:
func (s *Server) agentDiscoveries(w http.ResponseWriter, r *http.Request) {
    // 1. Auth (existing middleware)
    // 2. Decode request
    var req proto.DiscoveryRequest
    json.NewDecoder(r.Body).Decode(&req)

    // 3. Hand off to the Ingestor
    result, err := s.ingestor.Ingest(r.Context(), "agent-discovery", &req)

    // 4. Respond
    writeJSON(w, http.StatusOK, proto.DiscoveryResponse{Accepted: result.Accepted})
}
```

The polled-source scheduler uses the same entry point:

```go
// Scheduler loop (server-side polled sources)
for _, src := range store.ListEnabledSources() {
    if !due(src) { continue }
    payload, err := s.ingestor.PollSource(ctx, src)  // wraps adapter.Poll + Ingest
    store.RecordSourcePollResult(src.ID, err)
}
```

**Invariants:**

1. **Handlers do not import the adapter package.** A grep verifies this; the Foundation Critic flags violations.
2. **The Ingestor does not contain merge logic, SQL, or entity resolution.** It is glue: adapter lookup, resolver construction, pipeline call, result shaping. Anything beyond that belongs in the pipeline.
3. **One batch per `Ingest` call.** Each call constructs a fresh `IdentityResolver` (per the rule above) and opens at most one pipeline transaction.

---

## Error Handling

- **Adapter errors** (malformed input): Skip the bad record, log warning, continue processing remaining records in batch. Return count of accepted vs. rejected.
- **Identify errors** (DB read failure): Fail the entire batch. Return 500 to caller. Agent will retry.
- **Merge conflicts** (impossible state): Log error with full context, skip the conflicting field, continue. Never crash the pipeline.
- **Store errors** (DB write failure): Roll back transaction, return 500. Agent will retry.
- **Derive errors** (computation failure): Log warning, skip the derivation. Entity state is still consistent from Merge — derivations add richness but aren't required for correctness.

---

## Batch Processing

Observations within a batch are processed sequentially (not in parallel) to avoid race conditions on entity resolution. Batches from different sources can run concurrently — SQLite WAL mode handles concurrent reads, and write serialization is at the transaction level.

The pipeline holds no in-memory state between batches. All state lives in the database. This means the server can restart mid-processing without corruption — the incomplete transaction rolls back, and the source retries.
