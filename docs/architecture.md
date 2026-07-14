# Network Discovery — Architecture

A system for collecting, normalizing, and storing facts about nodes on a network.

---

## Core Concept: Facts

A **fact** is a single piece of information known about a node at a point in time.
Facts are the universal currency of the system — collected, transmitted, stored, and queried as facts.

### Fact ID format

```
Device[router-1].Interface[eth0].Speed
Device[router-1].Inventory.Modules[card0].SerialNumber
Network[10.0.0.0/24].Origin
CollectedAt
```

Rules:
- **Bracket pairs `[key]`** denote list membership — many of this entity exist, and a key identifies which one.
- **Bare names** (no brackets) are singletons or leaf attributes.
- **Dots** separate path segments. Dots inside brackets (IP addresses, CIDR prefixes) are part of the key and do not split the path.

### Structural path (attribute_path)

The same path with **empty brackets** marks list positions without embedding keys:

```
Device[].Interface[].Speed           ← structural form of Device[router-1].Interface[eth0].Speed
Device[].Inventory.Modules[].SerialNumber
Network[].Origin
```

The structural form answers "what shape is this fact?" independently of "which instance?" Used as the `attribute_path` column in storage, enabling efficient queries across all devices or all interfaces without LIKE scans on embedded key strings.

### Fact value types

| Kind | Storage | Examples |
|---|---|---|
| String | value_str TEXT | hostname, vendor, OS version |
| Long | value_long BIGINT | bytes, counts, ticks |
| Double | value_double DOUBLE PRECISION | percentages, temperatures, MHz |
| Bool | value_long (1/0) | enabled, up, reboot_required |
| DateTimeOffset | value_long (UTC ticks) | boot time, last seen |
| TimeSpan | value_long (ticks) | uptime, session duration |
| IPv4Address | value_long (uint) | interface address |
| IPv6Address | value_long + value_long2 | interface address |
| IPPrefix | value_long + value_long2 | subnet, route |
| MacAddress | value_long (48-bit) | interface identifier |

---

## Node Types

Nodes are the top-level entities in the fact tree. Each has a different identity model.

### Device

A physical or virtual network appliance that can be communicated with directly (SSH, SNMP, mDNS, DHCP, etc.).

**Identity:** server-assigned UUID, stable for the lifetime of the physical device. Established via fingerprint resolution on first contact; cached by the collector.

**Fingerprints (priority order):**

| Type | Normalized form | Notes |
|---|---|---|
| `chassis-serial` | `{vendor}:{serial}` e.g. `cisco:ftx2144abcd` | Best — survives IP/hostname changes |
| `uuid` | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` | VMs/virtual appliances |
| `snmp-engine-id` | lowercase hex | Secondary; can be reset |
| `ssh-host-key` | `{algorithm}:{hash}` | SSH-only; stable unless key regenerated |
| `mac` | 12 lowercase hex digits | Non-LA, non-multicast only |
| `bgp-router-id` | canonical IPv4 | Reconfigurable — tertiary |
| `ospf-router-id` | canonical IPv4 | Same caveats |

**Fingerprint normalization rules:**

| Type | Rules |
|---|---|
| `mac` | strip `:.−` separators; lowercase; reject LA bit (0x02), multicast bit (0x01), all-zeros, broadcast |
| `chassis-serial` | `{vendor}:{serial}`; vendor slug (rules only, no lookup table); serial lowercased; reject < 4 chars, all-same-char, known placeholders |
| `uuid` | strip `{}`, canonical lowercase with dashes; reject nil UUID |
| `snmp-engine-id` | strip `0x` + separators; lowercase; 10–64 hex chars; reject all-zeros |
| `ssh-host-key` | `{algorithm}:{hash}`; algorithm lowercased; SHA256/SHA1 verbatim; MD5 colons stripped |
| `bgp-router-id` | `IPAddress.Parse().ToString()`; reject 0.0.0.0, 255.255.255.255, 127.x.x.x, IPv6 |
| `ip-prefix` | zero host bits; `{addr}/{len}`; IPv4 and IPv6 |
| `route-distinguisher` | `{asn-or-ip}:{value}`; require dot for IP form (bare integers parse as IPv4 in .NET); reject 0:0 |
| vendor (component) | lowercase; non-alphanumeric runs → single `-`; no lookup table — caller provides canonical name |

**Child entities** (modeled as fact sub-paths):
- `Interface[mac]` — physical/logical interfaces
- `System` — OS info, uptime, load
- `Hardware` — CPU, memory, physical specs
- `Disk[serial]` — storage devices and SMART health
- `Filesystem[mountpoint]` — mounted filesystem usage
- `Docker` — Docker engine summary
- `Container[id]` — Docker container state and resource usage
- `Security` — firewall, antivirus, TPM, SecureBoot
- `Battery` — portable device battery state
- `Updates` — pending OS/package updates
- `Vrf[name]` — per-device VRF bindings
- `Vlan[id]` — VLAN membership and state

> **Route table note:** Route entries (`Vrf[name].Route[prefix]`) are high-cardinality children (100K+ per device). Do NOT use the standard projection pipeline. Store only in `facts_history`; query directly. Summary facts (route count, default route) are fine as projections.

---

### Network (IP prefix)

A routable IP prefix. The prefix itself is the global identity — no server-assigned UUID needed. `Network[10.0.0.0/24]` is the same node regardless of which device reports it.

**Identity:** canonical CIDR prefix. Host bits zeroed on normalization — `10.0.0.1/24` and `10.0.0.0/24` are the same node.

**Fact path:** `Network[10.0.0.0/24].Origin`, `Network[2001:db8::/32].AdvertisingDevice[{uuid}].Metric`

---

### VRF (MPLS L3VPN context)

A VRF is a device child in simple networks. In MPLS L3VPN it becomes top-level when identified by route distinguisher.

**Identity:** `route-distinguisher` fingerprint (e.g. `65000:100`) when cross-device; otherwise device child only.

**Fact paths:**
- Cross-device VPN: `Vrf[65000:100].Device[{uuid}].RouteCount`
- Per-device: `Device[{uuid}].Vrf[RED].InterfaceCount`

---

### VLAN

Relative identity only — VLAN 100 on switch A may or may not be the same L2 domain as on switch B. Model as a device child; cross-device VLAN topology is a query result, not a node type.

---

## Collection Pipeline

```
Device
  ↓
1) Identification   — what kind of node? Type, vendor, OS version
2) Fingerprinting   — collect all stable identifiers
   ↓  POST /devices/identify [{type, value}...]
   Server resolves stable DeviceId (UUID)
3) Collection       — gather all data via available protocols
4) Analysis         — normalize raw values, derive computed facts
   ↓  POST /ingest/{deviceId}  FactBatch (gzip-compressed JSON)
Server → facts_history + projections
```

---

## Analysis Pipeline

Analysis transforms raw collected data into canonical facts before they reach the server.

### Normalization

**Single-value transform. No external context.**

A normalizer is registered against an `attribute_path` pattern. When a fact with that path arrives, the normalizer transforms its value in place. Returns null to drop the fact (invalid value).

Examples:
- `"1Gbps"` string → `1_000_000_000L` (bps)
- `"XX:XX:XX:XX:XX:XX"` MAC string → `"xxxxxxxxxxxx"` normalized hex
- `"unknown"` speed → null (drop the fact)

### Derivation

**Multi-value computation. Requires context.**

A derivation declares:
- **Inputs** — explicit attribute_path patterns of required facts
- **Outputs** — attribute_path templates this derivation produces (for dependency ordering)
- **Scope** (optional) — explicit dimension grouping override; inferred from input intersection when null

**Scope inference:** intersect the list dimension names across all input patterns.
- `["Device[].Interface[].RxBytes", "Device[].Interface[].TxBytes"]` → scope `[Device, Interface]` — one run per (device, interface)
- `["Device[].Interface[].Name", "Device[].Vlan[].Id"]` → scope `[Device]` — one run per device, receiving all interfaces + VLANs
- `["Device[].Interface[].Enabled"]` with explicit `Scope = ["Device"]` → aggregate across all interfaces per device

**Layering:** derivations run in topological order based on declared `Outputs`/`Inputs` dependencies. A derived fact is immediately available as input to downstream derivations.

**Missing inputs:** if any required input is absent, the derivation produces no output. No error.

**No derived/observed flag:** the same fact may be observed on one device and derived on another. The fact ID is the identity; provenance is not stored.

### ID construction

`AnalysisEngine.BuildId(template, contextFact)` fills scope keys into an attribute_path template:
```
"Device[].Interface[].TotalBytes" + Device[r1].Interface[eth0].RxBytes → "Device[r1].Interface[eth0].TotalBytes"
```

---

## Collector / Server Contract

### Collector responsibilities
- **Delta tracking**: track last-sent value per fact ID; only transmit changed facts. This is the primary write-reduction mechanism — the server relies on it at scale.
- **Rollback**: on failed send, revert tracker state so the next cycle retries the unsent facts.
- **Fingerprint resolution**: call `POST /devices/identify` on first contact or when local DeviceId cache is cold. Cache the DeviceId; don't call on every cycle.
- **Batch format**: `FactBatch { CollectorId, DeviceId, CollectedAt, Facts[] }` — gzip-compressed JSON.

### Server responsibilities
- **History append**: insert facts into `facts_history` only when value differs from the most recent stored value (LATERAL LIMIT 1 dedup via covering index — index-only, no heap reads).
- **Projection updates**: route facts to matching `GenericProjection` instances; update projection rows only when entity state changed (EntityStateCache guard + SQL WHERE guard).
- **Device affinity**: all facts for a given device should reach the same server instance (hash on DeviceId at load balancer) for EntityStateCache coherence.

---

## Storage Design

### facts_history (the only fact table)

Append-only change log. One row per observed value change per fact ID.

```sql
facts_history (
    id             TEXT        PRIMARY KEY component,  -- full path: "Device[r1].Interface[eth0].Speed"
    attribute_path TEXT,       -- structural path: "Device[].Interface[].Speed"
    key_values     JSONB,      -- {"Device":"r1","Interface":"eth0"}
    kind           SMALLINT,
    value_str      TEXT,
    value_long     BIGINT,
    value_double   DOUBLE PRECISION,
    collected_at   TIMESTAMPTZ,
    PRIMARY KEY (id, collected_at)
)
```

**Why `attribute_path` + `key_values` instead of `entity_path` + `attribute`:**
- `entity_path = "Device[r1].Interface[eth0]"` embeds keys in a string → LIKE scans to find all eth0 interfaces across devices
- `attribute_path = "Device[].Interface[].Speed"` + `key_values = {"Device":"r1","Interface":"eth0"}` → `WHERE key_values->>'Interface' = 'eth0'` uses GIN index; `WHERE attribute_path = 'Device[].Interface[].Speed'` uses B-tree

**Indexes:**
- `(id, collected_at DESC) INCLUDE (kind, value_str, value_long, value_double)` — covering index for dedup CTE (index-only, no heap reads)
- `(attribute_path text_pattern_ops, collected_at DESC)` — structural path queries
- `GIN (key_values)` — key-value queries
- `(collected_at DESC)` — time range sweeps

### Projection tables (current state)

One row per entity, updated when any tracked fact changes. The projections ARE the current-state view — there is no separate current-state facts table.

| Table | Key | Contents |
|---|---|---|
| `proj_devices` | device | Vendor, Kind |
| `proj_systems` | device | OS, hostname, uptime, CPU%, memory, load |
| `proj_hardware` | device | CPU model/cores/MHz, memory, vendor, model, BIOS |
| `proj_interfaces` | device + interface (MAC) | Speed, MTU, state, counters, TotalBytes |
| `proj_disks` | device + disk (serial) | Size, type, SMART health |
| `proj_filesystems` | device + mountpoint | Usage bytes, UsedPercent |
| `proj_docker` | device | Engine version, container counts |
| `proj_containers` | device + container_id | State, health, resource usage |
| `proj_security` | device | Firewall, AV, TPM, SecureBoot |
| `proj_batteries` | device | Capacity, health, state |
| `proj_updates` | device | Pending count, security count, reboot required |

**Write guard:** projection rows are only written when at least one tracked column changed. Two layers:
1. EntityStateCache (application) — drops entities whose combined state hasn't changed before touching Postgres
2. SQL WHERE guard — `(EXCLUDED.col IS NOT NULL AND table.col IS DISTINCT FROM EXCLUDED.col) OR ...` — blocks writes when the DB already has the same value

**Device affinity requirement:** EntityStateCache is per-server-instance. Facts for the same device must reach the same instance, or the cache will diverge and may suppress writes that should happen.

### Dedup mechanism

History writes use a LATERAL LIMIT 1 lookup:

```sql
CROSS JOIN LATERAL (
    SELECT fh.kind, fh.value_str, fh.value_long, fh.value_double
    FROM   facts_history fh
    WHERE  fh.id = b_ids.id
    ORDER  BY fh.collected_at DESC
    LIMIT  1
) l
```

- One index seek per unique ID in the batch; stops at the most recent row regardless of history depth
- Covering index makes this index-only (no heap reads)
- Verified via EXPLAIN (ANALYZE, BUFFERS) on 500K-row table

---

## Scale Considerations (80K device networks)

At 80K devices × ~1000 facts each = 80M facts system-wide:

**Collector-side delta tracking is mandatory.** At 5% change rate per cycle = 4M changed facts. Without delta tracking, the server would receive 80M facts per cycle, which is not manageable.

**Batch per device** (not streaming): one HTTP POST per device per poll cycle. Fact IDs share long common prefixes → 80–90% gzip compression. Stateless request handling enables horizontal scaling.

**Route tables need separate treatment.** A device with 100K route entries produces a 500K-fact batch. Do not project route tables — store in `facts_history` only and query directly.

**Chunk size for DB writes:** 10K facts per SQL round-trip (tunable). Caps lock duration and `work_mem` usage.

**Postgres tuning:**
- `shared_buffers` large enough to keep hot covering index pages resident
- `autovacuum_vacuum_scale_factor` tuned lower for projection tables (frequent upserts)
- `work_mem` sufficient for 10K-row unnest operations
- Monthly partitioning of `facts_history` (`PARTITION BY RANGE (collected_at)`) enables cheap old-data removal

---

## Go Application Data

The Go agent collects two payloads:

| Payload | Frequency | Contents |
|---|---|---|
| `MetricSnapshot` | High (every ~30s) | CPU%, memory used/total, load averages, uptime, per-interface byte/packet counters, per-disk usage |
| `Inventory` | Low (every ~24h) | Hardware, OS, disks (with SMART), network config, Docker, containers, security posture, battery, update status |

Key fact paths by entity — see `FactPaths.cs` for complete listing.

**Normalizations needed:**
- Link speed: Go provides `LinkSpeedMbps int` → collector converts to bps (`× 1_000_000`) before creating fact
- MAC address: Go provides `"XX:XX:XX:XX:XX:XX"` → `MACAddressNormalizer` strips separators, lowercases, rejects LA/multicast

**Key derivations:**
- `Interface.TotalBytes` = `RxBytes + TxBytes` (interface scope)
- `Filesystem.UsedPercent` = `UsedBytes / TotalBytes × 100` (filesystem scope)
- `System.MemUsedPercent` = `MemUsedBytes / MemTotalBytes × 100` (device scope)
- `Battery.HealthPercent` = `CurrentCapacityWh / DesignCapacityWh × 100` (device scope)
