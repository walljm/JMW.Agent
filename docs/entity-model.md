# Entity Model

The data model operates at multiple scales. Entities have explicit relationships — both hierarchical (parent-child) and lateral (observation source, network membership, service dependency).

## Promotion Criteria — When a Field Becomes an Entity

**Default position:** if the system reports on it, it gets a real table. JSON blobs are reserved for two narrow cases (see below), not for hiding data the operator will want to query.

A reported field MUST be modeled as a first-class table (or extension table) when any of these apply:

1. **Queryable across hosts** — "show me every host where X" requires SQL filtering, not blob scanning.
2. **Alertable** — a threshold or state change on this field should be able to fire an alert.
3. **Time-series valuable** — we want trend, history, or rollups, not just a snapshot.
4. **Cross-referenced** — another entity needs to join to it.

A field MAY stay as a JSON column (on its parent entity) only when ALL of these apply:

- It is small (< 4 KB), bounded, and rarely changes.
- It is rendered as a structured panel on a single detail view, never aggregated across rows.
- No alert, filter, search, or report needs to read inside it.

A field MUST NOT be hidden in an opaque archive blob. The previous `inventory_json` column on Agent is **deprecated** — every payload section it once held is promoted to a real table below. The blob remains only for forward-compat: if a *new* inventory section is added by the agent before the server has a table for it, it lands in `agent_unknown_sections` (key = section name) with a one-cycle TTL until a migration adds the proper table. This is a tripwire, not a long-term home.

## Time-Series vs. State

Tables in this document split into two kinds:

- **State tables** — current best-known value of a thing. One row per entity, updated by the pipeline. Examples: Hardware, System, Service, `update_status`, `security_posture`.
- **Snapshot tables** — time-stamped samples. Many rows per entity. Examples: `metric_snapshots`, `filesystem_snapshots`, `temperature_snapshots`, `battery_snapshots`. Rolled up and pruned per the retention policy in `metrics-and-alerting.md`.

When the same data has both a current value and a useful history, it gets BOTH: a state table for "now" and a snapshot table for "over time" (e.g., `disks` state + `disk_snapshots` time-series).

## Entity Scales

```
Network (largest)
 └── contains Interfaces (via IP/subnet membership)

Hardware (physical chassis)
 ├── has hardware_cpu, hardware_memory, hardware_board, hardware_bios, hardware_gpus
 └── hosts Systems

System (computing entity)
 ├── has Interfaces (network presences)
 ├── runs Services (software processes)
 ├── has Disks (storage devices)
 ├── has update_status, security_posture, failed_services, listening_ports,
 │       routes, local_users, logged_in_sessions, packages, hassio_info
 └── hosts child Systems (containers, VMs)

Interface (network attachment)
 ├── has Interface Addresses (zero or more IPs)
 ├── has interface_profile (structured probe data, derived)
 └── has Observations (what sources told us)

Service (running software)
 └── (monitored only — observation sources are a separate Source entity)

Source (pipeline input)
 └── adapters, pollers, agent feeds — may point to a Service when discovered
```

## Relationship Diagram

```
                    ┌──────────┐
                    │ Network  │
                    └────┬─────┘
                         │ contains (by CIDR)
                         ▼
┌──────────┐       ┌──────────┐       ┌──────────────┐
│ Hardware │──hosts─▶│  System  │──has──▶│  Interface   │
└──────────┘       └─────┬────┘       └──────┬───────┘
                         │                    │
              ┌──────────┼──────────┐         │ has
              │          │          │         ▼
         ┌────▼───┐ ┌────▼───┐ ┌───▼────┐  ┌─────────────┐
         │Service │ │  Disk  │ │ Child  │  │ Observation │
         └────────┘ └────────┘ │System  │  └─────────────┘
                                └────────┘
                         ▲
                         │ optionally references
                         │
                    ┌────┴────┐
                    │ Source  │ (pipeline input — adapter or poller)
                    └─────────┘
```

---

## Network

A logical network segment. Identified by the MAC address of its gateway (the most stable anchor — IPs and SSIDs can change, but the gateway MAC persists until hardware replacement).

| Field | Description |
|-------|-------------|
| id | Server-generated UUID |
| gateway_mac | Stable identity anchor (unique constraint) |
| cidr | Subnet in CIDR notation (e.g., 192.168.1.0/24) |
| name | User-friendly label (auto-generated from SSID or CIDR, user-overridable) |
| ssid | Wi-Fi network name (empty for wired-only segments) |
| status | `discovered` / `monitored` / `ignored` |
| discovered_by | Agent ID that first reported this network's gateway |
| created_at | When first seen |
| updated_at | Last observation of any interface on this network |

**Identity:** Gateway MAC is globally unique and survives DHCP changes, SSID renames, and subnet renumbering. A network entity is created when an agent reports a `NetworkContext` with a gateway MAC not yet in the store.

**Membership:** An interface belongs to a network when its current IP falls within the network's CIDR. This is a derived relationship (computed in the Derive stage), not manually assigned. An interface can appear on multiple networks over time (laptop roaming), tracked via a junction table with timestamps.

**Status lifecycle:**
- `discovered` — seen by an agent but not explicitly managed
- `monitored` — admin has opted in to track this network
- `ignored` — admin has explicitly excluded (e.g., guest VLAN noise)

---

## Hardware

A physical device. Has mass. Occupies space. Cannot be created by software.

| Field | Description |
|-------|-------------|
| id | Server-generated UUID |
| serial | DMI/SMBIOS serial number (if known) |
| vendor | Manufacturer (Dell, Synology, Apple, Ubiquiti, ...) |
| model | Model name/number |
| form_factor | `server` / `desktop` / `laptop` / `sbc` / `appliance` / `switch` / `ap` / `phone` / `tablet` / `unknown` |
| identity_source | How we established this entity: `agent` (definitive) / `correlation` (inferred) / `user` (manually created) |
| identity_confidence | `high` / `medium` / `low` |
| identity_evidence | JSON array of reasons: `["agent:abc reports serial XYZ", "SSH key matches across en0+en1"]` |
| notes | User-provided description |
| created_at | When first established |
| updated_at | Last time any child entity was observed |

**Identity rules:**

| Scenario | Identity source | Confidence | Key |
|----------|----------------|------------|-----|
| Agent reports DMI serial + all NICs | `agent` | high | agent_id (definitive) |
| Agent reports NICs but no serial | `agent` | high | agent_id (still definitive — agent knows its own hardware) |
| Multiple interfaces grouped by SSH host key | `correlation` | high | SSH key SHA-256 |
| Multiple interfaces share mDNS hostname | `correlation` | medium | hostname pattern |
| Multiple interfaces share NBNS name | `correlation` | medium | hostname pattern |
| Single interface, no grouping evidence | `correlation` | low | created as 1:1 with the interface |
| Admin manually groups interfaces | `user` | high | user override (locked) |

**Lifecycle:**
- Hardware entities are never automatically deleted. A device that goes offline is still hardware that exists.
- Admin can archive hardware to remove it from active views.
- Hardware with zero observations across all children for > configurable threshold gets flagged as "stale" in the UI.

### hardware_cpu

CPU facts. One row per Hardware entity (1:1 extension table).

| Field | Description |
|-------|-------------|
| hardware_id | FK → Hardware (PK) |
| model | CPU model string ("Intel Xeon E-2226G", "Apple M2 Pro") |
| vendor | Manufacturer ("Intel", "AMD", "Apple", "ARM") |
| arch | `amd64` / `arm64` / `arm` / `riscv64` / ... |
| cores_physical | Physical core count |
| cores_logical | Logical/hyperthread count |
| base_mhz | Base clock |
| updated_at | Last refresh |

### hardware_memory

RAM facts. One row per Hardware entity.

| Field | Description |
|-------|-------------|
| hardware_id | FK → Hardware (PK) |
| total_bytes | Total installed RAM |
| updated_at | Last refresh |

### hardware_board

Motherboard / system board facts.

| Field | Description |
|-------|-------------|
| hardware_id | FK → Hardware (PK) |
| board_vendor | Board manufacturer |
| board_model | Board model |
| system_vendor | System (chassis) vendor |
| system_model | System model |
| updated_at | Last refresh |

### hardware_bios

Firmware / BIOS facts.

| Field | Description |
|-------|-------------|
| hardware_id | FK → Hardware (PK) |
| vendor | BIOS/firmware vendor (AMI, Insyde, Apple, ...) |
| version | BIOS version string |
| release_date | BIOS release date (DMI field) |
| virtualization | Detected virtualization: `none` / `kvm` / `vmware` / `hyperv` / `docker` / `wsl` / ... |
| updated_at | Last refresh |

**Why an extension instead of inline:** keeping hardware identity (serial/vendor/model/form_factor) separate from hardware *facts* (CPU/RAM/board/BIOS) lets identity correlation queries stay small and lets facts evolve (e.g., RAM upgrades) without rewriting the identity row's `updated_at`.

### hardware_gpu

One row per GPU on the hardware. Many-per-hardware.

| Field | Description |
|-------|-------------|
| hardware_id | FK → Hardware |
| slot_index | 0-based position on the host |
| vendor | NVIDIA / AMD / Intel / Apple / ... |
| model | GPU model |
| driver_version | Current driver version |
| vram_bytes | Video memory |
| first_seen_at | First report |
| last_seen_at | Last report |

**Key:** `(hardware_id, slot_index)`.

### hardware_temperatures (time-series)

Point-in-time thermal samples. See [temperature_snapshots](#temperature_snapshots) in the Snapshot Tables section — temperatures are time-series, not state, even though they're sourced from hardware. The current value is the latest snapshot.

---

## System

A logical computing entity running on hardware. The OS, a VM, a container, router firmware.

| Field | Description |
|-------|-------------|
| id | Server-generated UUID |
| hardware_id | FK → Hardware |
| type | `bare-metal` / `vm` / `container` / `firmware` |
| lifecycle | `persistent` / `ephemeral` |
| hostname | Canonical name (priority-aware, best source wins) |
| hostname_source | Which source provided the canonical hostname |
| os_family | `linux` / `darwin` / `windows` / `freebsd` / `firmware` / `unknown` |
| os_distro | Ubuntu 24.04, macOS 15.1, Synology DSM 7.2, ... |
| os_version | Version string |
| os_build | OS build string (Windows build, Darwin build) |
| kernel_version | Kernel/firmware version when known |
| kernel_arch | Kernel architecture string (e.g., `x86_64`, `aarch64`) |
| boot_time | When the OS last booted |
| install_date | When the OS was originally installed (when reported) |
| timezone | IANA timezone name (e.g., `America/Denver`) |
| agent_id | FK → agents table (if this system runs our agent) |
| container_id | 64-char hex SHA (for Docker/Podman containers) |
| container_image | Image name:tag (for containers) |
| host_system_id | FK → System (self-referential: which system hosts this container/VM) |
| state | `running` / `stopped` / `offline` / `removed` |
| removed_at | When state became `removed` (for retention pruning) |
| first_seen_at | Earliest observation |
| last_seen_at | Most recent observation |
| labels_kv | See [system_labels](#system_labels) — labels are a child table, not a JSON column on this row |

**Type semantics:**

| Type | Lifecycle | Parent | Identity | Example |
|------|-----------|--------|----------|---------|
| bare-metal | persistent | hardware directly | agent_id or hostname correlation | macOS on a MacBook |
| vm | persistent or ephemeral | host system | VM UUID or hostname | Proxmox guest |
| container | ephemeral | host system | container_id on host | Docker nginx container |
| firmware | persistent | hardware directly | hardware identity | Ubiquiti switch OS |

**Container lifecycle:**
- Created when host agent reports it in inventory.
- `state = running` / `stopped` based on container state field.
- `state = removed, removed_at = now` when absent from next inventory sync.
- Pruned from database after retention period (default 7 days after removal).
- Recreated containers (same image, new container_id) are new system entities — we don't try to correlate across recreations.

**State transitions:**
```
                    inventory reports it
         ┌─────────────────────────────────────┐
         │                                     ▼
    ┌─────────┐  container stops  ┌─────────────────┐
    │ running │──────────────────▶│    stopped      │
    └─────────┘                   └────────┬────────┘
         ▲                                 │
         │  container starts               │ absent from inventory
         │                                 ▼
         │                        ┌─────────────────┐   after retention
         └────────────────────────│    removed      │──────────────────▶ DELETE
                                  └─────────────────┘
```

For persistent systems (bare-metal, VMs):
```
    ┌─────────┐  no observation for threshold  ┌─────────────────┐
    │ running │───────────────────────────────▶│    offline      │
    └─────────┘                                └────────┬────────┘
         ▲                                              │
         │  new observation arrives                     │ never auto-deleted
         └──────────────────────────────────────────────┘
```

### system_labels

Key-value labels attached to a System. Covers Docker labels, docker-compose metadata, user annotations, and any future label-like attribute.

| Field | Description |
|-------|-------------|
| system_id | FK → System |
| key | Label key (lowercase, normalized) |
| value | Label value |
| source | Where the label came from: `docker` / `compose` / `user` / `agent` |
| updated_at | Last refresh |

**Key:** `(system_id, key)`.

**Rationale:** previously stored as a JSON blob on `System.labels`. Promoted to a real table because users will filter device lists by label (`?label=env:production`) and because Docker label counts can be large enough to bloat row scans.

---

## Interface

A network attachment point. A MAC address on a wire or radio.

| Field | Description |
|-------|-------------|
| id | Normalized lowercase MAC address (primary key) |
| system_id | FK → System |
| type | `physical` / `virtual` / `bridge` / `tunnel` / `random` / `unknown` |
| vendor | OUI lookup result |
| name | OS interface name when known (en0, eth0, docker0, br-xxx) |
| vlan_id | 802.1Q VLAN tag if known (observed from interface config or user-provided) |
| is_local | True if locally-administered bit (bit 1, first octet) is set — MAC assigned by software, not OUI-registered |
| first_seen_at | Earliest observation of this MAC |
| last_seen_at | Most recent observation |
| last_seen_by | Agent/source that last observed this interface |

**Addresses:** An interface has zero or more IP addresses (see [Interface Address](#interface-address) below). A single NIC can carry multiple IPv4 (aliases, secondaries) and multiple IPv6 (link-local + global + privacy). Addresses are tracked as child rows, not scalar fields on Interface.

**Identity:**
- Primary key = MAC address (globally unique for non-local addresses).
- Locally-administered MACs (bit 1 of first octet set): tracked but flagged `is_local = true`. Cannot be used for cross-network identity correlation. OUI vendor lookup is meaningless for these.
- Multicast MACs (bit 0 of first octet set): **rejected at ingest** — these are group addresses, not real devices. Never stored as Interface entities.

**Type classification:**

| Type | How determined | Examples |
|------|---------------|----------|
| physical | Agent reports it as a hardware NIC | en0, eth0, wlan0 |
| virtual | Agent reports it as a virtual adapter | utun*, awdl0, llw0 |
| bridge | Interface name matches bridge patterns | docker0, br-*, virbr*, lxcbr* |
| tunnel | VPN/overlay interface | wg0, tun0, vxlan* |
| random | Apple Private Wi-Fi Address or similar | Detected by OUI or local-admin bit |
| unknown | Seen via discovery only (no agent context) | Default for unmanaged |

**MAC randomization handling:**
- Apple devices rotate Wi-Fi MAC per-network (Private Wi-Fi Address).
- These MACs have the locally-administered bit set → flagged `is_local = true`.
- The correlation engine can still group them to hardware via other evidence (mDNS device-info TXT records contain model identifier, or agent self-reports all MACs including the random one).
- No automatic grouping based solely on "same hostname, different local MAC" — too many false positives. Requires high-confidence evidence or user confirmation.

### interface_profile

Structured probe results materialized by the Derive stage. One row per Interface that has any probe data. Replaces the deprecated `latest_profile_json` blob column.

| Field | Description |
|-------|-------------|
| interface_id | FK → Interface (PK) |
| mdns_hostname | mDNS `.local` name |
| mdns_services | See [interface_mdns_services](#interface_mdns_services) below |
| mdns_txt_kv | See [interface_mdns_txt](#interface_mdns_txt) below |
| eureka_name | Google Cast / Eureka friendly name |
| eureka_model | Cast / Eureka model identifier |
| ipp_printer_name | IPP-advertised printer name |
| ipp_make_model | IPP make+model string |
| roku_name | Roku device name |
| roku_model | Roku model |
| airplay_name | AirPlay name |
| ssh_host_key_sha256 | SSH host key fingerprint (hex SHA-256) |
| ssh_banner | SSH server banner string |
| ldap_root_dse | LDAP rootDSE dnsHostName (AD DCs) |
| http_title | `<title>` from HTTP root |
| tls_cn | TLS cert Common Name |
| tls_sans | See [interface_tls_sans](#interface_tls_sans) below |
| smb_machine_name | SMB/CIFS machine name |
| dhcp_announced_hostname | Client-announced hostname from DHCP |
| updated_at | When Derive last refreshed this row |

**Why a structured table, not a blob:** every column here is queryable (find all printers, find all hosts with a given SSH host key, find AirPlay devices by name). Bools, strings, and short identifiers belong in columns. Multi-value sub-fields (mDNS services, TXT records, TLS SANs) get their own child tables below — never JSON arrays.

**Derivation:** the Derive stage reads recent observations for this interface, picks the best-priority value per field, and writes one row. This is a render cache — the source of truth is the underlying observation rows. The Derive stage can rebuild any `interface_profile` row from observations alone.

### interface_mdns_services

Each mDNS service type advertised by this interface.

| Field | Description |
|-------|-------------|
| interface_id | FK → Interface |
| service_type | mDNS service (e.g., `_airplay._tcp`, `_ipp._tcp`, `_http._tcp`) |
| port | Port the service advertises on |
| first_seen_at | First observation |
| last_seen_at | Last observation |

**Key:** `(interface_id, service_type, port)`.

### interface_mdns_txt

mDNS TXT record key-value pairs.

| Field | Description |
|-------|-------------|
| interface_id | FK → Interface |
| service_type | Which service the TXT belongs to (`_airplay._tcp`, ...) |
| key | TXT key |
| value | TXT value |
| updated_at | Last refresh |

**Key:** `(interface_id, service_type, key)`.

### interface_tls_sans

TLS certificate Subject Alternative Names observed on a port of this interface.

| Field | Description |
|-------|-------------|
| interface_id | FK → Interface |
| port | TLS port |
| san | One SAN entry (DNS name or IP) |
| not_after | Cert expiry (denormalized for convenience) |
| updated_at | Last refresh |

**Key:** `(interface_id, port, san)`.

---

## Service

A software process or daemon running on a system. Service entities represent **monitored things**. Pipeline input sources are modeled separately as [Source entities](#source) — a Service that happens to be on a managed host may be *referenced* by a Source, but it does not own the source role.

| Field | Description |
|-------|-------------|
| id | Server-generated UUID |
| system_id | FK → System |
| name | Service name (e.g., "AdGuard Home", "Docker Engine", "nginx", "sshd") |
| type | `dns` / `dhcp` / `container-runtime` / `web` / `database` / `monitoring` / `other` |
| version | Version string when known |
| port | Primary listening port |
| protocol | `tcp` / `udp` / `both` |
| state | `running` / `stopped` / `unknown` |
| discovered_via | How we know about this service: `agent-inventory` / `probe` / `user` |
| first_seen_at | When first detected |
| last_seen_at | When last confirmed active |

**Why no `is_observation_source` here:** the old model conflated two roles. A Service is something we **monitor**. A pipeline input is a **Source** (see below). When the same physical thing plays both roles (e.g., the AdGuard daemon serves DHCP leases to the pipeline AND we want to alert if it goes down), it appears as both: a Service row (monitored) AND a Source row (input), linked by `Source.service_id`. Either side can exist alone — terrain sources can pre-date Service discovery, and most Services never feed the pipeline.

**Service config:** if a Service needs configuration to be polled as a Source (URL, credentials reference), that config lives on the corresponding Source row, not here.

**Discovery:**
- Services on managed hosts are reported by the agent (inventory includes listening ports, Docker status).
- Services on unmanaged hosts are detected via probes (mDNS service types, port scanning, banner grabbing).
- Service discovery and Source configuration are independent — discovering AdGuard via mDNS does not configure terrain polling against it. An admin (or a future autoconfig) explicitly links them by creating a Source row.

---

## Source

Pipeline input. A Source is anything that produces observations: an agent feed, a server-side poller (terrain DHCP/DNS, future SNMP, future Nmap), or a user-input adapter. Each Source has a kind, a status, and (if applicable) a link to the Service it polls.

| Field | Description |
|-------|-------------|
| id | Server-generated UUID |
| kind | `agent` / `terrain-dhcp` / `terrain-dns` / `snmp-poller` / `nmap-scanner` / `user-input` / future kinds |
| name | Operator-facing label ("AdGuard DHCP", "Office switch SNMP") |
| service_id | FK → Service (NULL if this source isn't tied to a monitored Service — e.g., the agent feed, an Nmap scan range) |
| agent_id | FK → Agent (set only for `kind=agent` sources) |
| config_json | Source-specific config (URL, poll interval, scan range, embedded secrets). Each kind's schema is documented in `observation-sources.md`. Secret fields inside this blob are encrypted at rest with the server DEK — see infrastructure.md → Secret Storage. |
| poll_interval_seconds | How often this source is polled (NULL for push-only sources like the agent feed) |
| last_success_at | Last successful poll/push |
| last_error_at | Last failed poll/push |
| last_error_message | One-line description of the last failure |
| consecutive_error_count | Reset to 0 on each success |
| enabled | Operator can disable a source without deleting it |
| created_at | When configured |
| updated_at | Last config change |

**Why a separate entity:** Sources have their own lifecycle (polling cadence, error budget, credentials, enable/disable). Services have a different one (running/stopped, version, health). Conflating them meant a Service row had to know about polling state and an adapter had to know about service health. Splitting them is the correct factoring; the original docs' "dual-role" framing was a workaround for the missing entity.

**Why not in `server.toml`:** Sources are runtime-mutable, credential-bearing, per-instance config. They are managed exclusively through the admin UI (CRUD over this table) — not via files on disk, not via environment variables, not via process flags. See infrastructure.md → What Belongs in `server.toml` vs the Database for the rationale.

**Reference:** the per-kind contract for Sources (poll schedule, retry policy, ingest channel) is defined in [observation-sources.md](observation-sources.md) under "Server-Side Polled Source" and "Adapter Registration."

---

## Disk

Physical or virtual storage attached to a system. Tracked for capacity planning and health monitoring (SMART).

| Field | Description |
|-------|-------------|
| id | Composite: system_id + device_path |
| system_id | FK → System |
| device_path | OS device path (/dev/sda, /dev/nvme0n1) |
| serial | Drive serial number |
| model | Drive model |
| vendor | Drive manufacturer |
| type | `hdd` / `ssd` / `nvme` / `usb` / `virtual` / `unknown` |
| size_bytes | Total capacity |
| smart_health | `ok` / `warning` / `failing` / `unknown` / `unsupported` |
| temperature_c | Current temperature (latest sample; history in `temperature_snapshots`) |
| first_seen_at | When first reported |
| last_seen_at | When last reported |

**Identity:** Drive serial number is globally unique (when available). For virtual disks or drives without serials, the composite (system_id, device_path) is used.

**Lifecycle:** Disks follow their system. When a system reports inventory, any disks absent from the report are marked stale but not deleted (drives can be temporarily unmounted).

### disk_smart_attributes

Per-attribute SMART data. One row per (disk, attribute_id). Replaces the deprecated `smart_json` blob.

| Field | Description |
|-------|-------------|
| disk_id | FK → Disk |
| attribute_id | SMART attribute number (e.g., 5 = reallocated sectors, 197 = pending sectors) |
| attribute_name | Human-readable name |
| value | Normalized current value (0–255 typically) |
| worst | Worst-ever normalized value |
| threshold | Failure threshold |
| raw | Raw vendor-defined value (string — varies by vendor) |
| status | `ok` / `warning` / `failing` |
| updated_at | Last refresh |

**Key:** `(disk_id, attribute_id)`.

### disk_partitions

One row per partition. Replaces the deprecated `partitions` JSON array.

| Field | Description |
|-------|-------------|
| disk_id | FK → Disk |
| partition_index | 0-based index on the disk |
| uuid | Partition UUID (when reported) |
| label | Filesystem label |
| filesystem | `ext4` / `apfs` / `ntfs` / `btrfs` / `xfs` / ... |
| mountpoint | Current mountpoint (NULL if not mounted) |
| size_bytes | Partition size |
| updated_at | Last refresh |

**Key:** `(disk_id, partition_index)`. Filesystem usage (free/used over time) is in [filesystem_snapshots](#filesystem_snapshots), not here — that's time-series, this is structure.

---

## Observation

An immutable, timestamped fact reported by a source. The raw input to the pipeline.

| Field | Description |
|-------|-------------|
| interface_id | FK → Interface (which MAC was observed) |
| source_type | Adapter identifier: `agent-discovery` / `agent-inventory` / `terrain-dhcp` / `terrain-dns` / `user` |
| source_id | Specific instance (agent UUID, terrain poller name) |
| method | Protocol/technique: `arp` / `mdns` / `nbns` / `dhcp` / `snmp` / `ssh` / `http` / `inventory` / ... |
| observed_at | When the source made this observation |
| ip | IP at time of observation |
| hostname | Name announced/discovered at observation time |
| data_json | Source-specific payload (TXT records, probe results, DHCP fields) |
| first_seen_at | Bucketed: first time this exact combo was seen |
| last_seen_at | Bucketed: most recent observation |
| count | Bucketing counter (increments on repeated identical observations) |

**Bucketing key:** `(interface_id, source_id, method, ip)` — prevents unbounded row growth. Same combination updates count + last_seen_at rather than inserting.

**Immutability:** Once written, observation rows are not modified (except the bucketing counter and last_seen_at). They represent "what was reported" — even if later determined incorrect. The Merge stage decides what to *believe*, not what to *record*.

---

## Hostname Aliases

All observed names for an interface/system, from all sources, across all time. Kept regardless of which one "won" the canonical hostname slot.

| Field | Description |
|-------|-------------|
| interface_id | FK → Interface |
| name | The observed hostname |
| source | Which protocol/technique reported it |
| first_seen_at | When first observed |
| last_seen_at | When most recently observed |
| count | How many times reported |

**Purpose:** The canonical `system.hostname` shows the best-priority name. But the aliases table preserves *all* observed names so the UI can surface conflicts, historical names, and multi-source agreement/disagreement.

---

## Interface Address

An IP address assigned to an interface. One interface can have many addresses (multiple IPv4 aliases, multiple IPv6 scopes, dual-stack).

| Field | Description |
|-------|-------------|
| interface_id | FK → Interface (MAC) |
| address | IP address (v4 or v6, no CIDR prefix) |
| family | `ipv4` / `ipv6` |
| prefix_len | Subnet prefix length (e.g., 24 for /24) |
| scope | `global` / `link-local` / `private` / `loopback` |
| source | Who reported this address: method name from observation |
| source_id | Specific reporter (agent UUID, terrain, etc.) |
| first_seen_at | When first observed on this interface |
| last_seen_at | When last confirmed active |
| is_primary | Derived flag: best address for display (one per family per interface) |

**Key:** `(interface_id, address)` — a MAC can only have a given IP once at a time.

**Lifecycle:**
- Created when any observation reports this IP for this MAC.
- `last_seen_at` updated on each re-observation.
- Marked stale (but not deleted) when a managed agent reports its interface config and this address is absent. Unmanaged interfaces never have addresses marked stale — they persist until manually cleaned.

**Primary derivation:** For each interface + family pair, the primary address is the one with the most recent observation from the highest-priority source. Used for display in list views and for network membership computation.

**Network membership:** An interface belongs to a network when any of its addresses falls within that network's CIDR. Computed in the Derive stage from this table.

---

## Agent

The registration and lifecycle relationship between a managed system and the server. Not a separate "thing" — it's the management plane record for a System that runs our software.

| Field | Description |
|-------|-------------|
| id | Agent-generated UUID (stable across restarts) |
| system_id | FK → System (the system this agent manages) |
| hostname | Self-reported hostname (denormalized — see "Denormalized field sync" below) |
| os | Operating system family (denormalized from System.os_family) |
| arch | CPU architecture (denormalized from hardware_cpu.arch) |
| version | Agent binary version |
| status | `pending` / `approved` / `deregistered` |
| registered_at | When the agent first contacted the server |
| approved_at | When an admin (or PSK) approved registration |
| approved_by | Who approved: username or "psk" |
| last_heartbeat_at | Most recent heartbeat timestamp |
| primary_ip | Denormalized best IP for list display (see "Denormalized field sync" below) |
| inventory_collected_at | When the latest inventory was taken |
| notes | Admin-provided notes |

**Relationship to System:** An Agent is a management record. The System entity represents the computing host; the Agent record represents our software's registration on that host. One System has at most one Agent. When an agent registers, the pipeline's Identify stage either finds an existing System (by hostname + serial correlation) or creates one.

**Raw inventory archive is removed.** There is no `inventory_json` column. Every section that used to live in the blob is now a real table (see the Promotion Criteria section at the top of this document). For audit/traceability without keeping the payload, the pipeline writes one row per ingest into:

### agent_inventory_receipts

| Field | Description |
|-------|-------------|
| id | Auto-increment |
| agent_id | FK → Agent |
| received_at | When the ingest occurred |
| byte_size | Compressed payload size |
| content_hash | SHA-256 of the canonicalized payload |
| accepted | Whether the pipeline accepted (vs rejected/quarantined) |
| reject_reason | NULL when accepted; short tag when not |
| unknown_sections | Comma-separated list of payload sections the server didn't recognize this cycle |

**Pruned per retention policy** (see `infrastructure.md` — same tier as other audit logs).

### agent_unknown_sections

Forward-compat tripwire. When the agent sends an inventory section the server has no table for yet, the parser writes a one-row-per-section record here so operators (and the server itself) can detect "the agent shipped a new metric we don't have a home for." Rows older than 7 days are pruned — they are not a data store.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| section_name | Payload section key (e.g., `nvme_namespaces`) |
| first_seen_at | When we first saw this section |
| last_seen_at | Most recent occurrence |
| sample_size_bytes | Size of the most recent sample |

**Key:** `(agent_id, section_name)`. The Foundation Critic should flag any agent_unknown_sections row older than one release cycle — that means a schema migration was missed.

### Denormalized field sync

`agents.hostname`, `agents.os`, `agents.arch`, and `agents.primary_ip` are denormalized for list view rendering. They are NOT the source of truth.

**Sync rule:** the Derive stage refreshes these four fields on the Agent row at the end of every inventory ingest for that agent. Specifically:

- `hostname` ← `system.hostname` (canonical, after Merge picks the winner)
- `os` ← `system.os_family`
- `arch` ← `hardware_cpu.arch`
- `primary_ip` ← best address from `interface_addresses` for this agent's interfaces (highest-priority source, IPv4 preferred, non-link-local, most-recently-seen)

These columns must never be written outside the Derive stage. The Foundation Critic should flag any other writer.

### enabled_subsystems → agent_subsystems (junction)

What used to be `enabled_subsystems` JSON array on Agent is now a junction table — see [agent_subsystems](#agent_subsystems) in the Junction Tables section, and the [subsystems](#subsystems) registry.

**Status lifecycle:**
```
  ┌─────────┐   admin/PSK approval   ┌──────────┐
  │ pending │────────────────────────▶│ approved │
  └─────────┘                         └─────┬────┘
       │                                    │
       │ expired (72h)                      │ admin deregisters
       ▼                                    ▼
    DELETE                           ┌──────────────┐
                                     │deregistered  │
                                     └──────────────┘
```

---

## Container Metadata

Docker/OCI-specific fields for systems with `type = container`. This is an extension table — not a separate entity — to avoid polluting the general System table with container-only columns.

| Field | Description |
|-------|-------------|
| system_id | FK → System (PK, 1:1 with the container system) |
| container_id | Docker container ID (64-char hex) |
| image | Image reference as run (tag or digest) |
| image_id | Image content hash (sha256:...) |
| command | Entrypoint + cmd flattened |
| compose_project | docker-compose project name |
| compose_service | docker-compose service name |
| health | `healthy` / `unhealthy` / `starting` / `none` |
| restart_policy | `no` / `always` / `on-failure` / `unless-stopped` |

Container labels live in [system_labels](#system_labels) (`source='docker'` or `source='compose'`). Mounts and port bindings live in [container_mounts](#container_mounts) and [container_ports](#container_ports). There is no `record_json` blob.

**Why an extension table, not inline on System:** Only ephemeral container systems have these fields. Keeping them separate avoids nullable columns on every System row and makes the container-specific queries cheaper (smaller scan set).

**Container networking scope:**

Docker containers have their own MACs and IPs (reported via `ContainerNetwork` in the agent inventory). Whether these become Interface entities depends on the network type:

| Docker network type | Creates Interface entity? | Creates Network entity? | Rationale |
|--------------------|--------------------------|------------------------|-----------|
| `host` | No (container shares host's interfaces) | No | No distinct network attachment |
| `bridge` (default) | Yes (container has unique MAC on bridge) | No — internal Docker bridges are **excluded** | 172.17.x.x addresses aren't routable from the LAN; creating Network entities for them would pollute the network list |
| User-defined bridge | Yes (unique MAC) | No — same as default bridge | Still internal-only |
| `macvlan` / `ipvlan` | Yes (container has a LAN-routable MAC/IP) | Yes (joins existing LAN Network entity) | Container appears as a real device on the LAN |
| `overlay` (Swarm) | No | No | Swarm-internal, not relevant to LAN monitoring |
| `none` | No | No | No networking |

**Key rule:** An Interface entity is created for a container ONLY if the container has a distinct MAC. A Network entity is created/joined ONLY if the container's IP is routable on a monitored LAN segment (falls within an existing Network's CIDR).

**Why exclude bridge networks:** The system's purpose is monitoring the *LAN* — what's visible to other devices on the network. Docker bridge IPs are invisible outside the host. Including them would:
- Create hundreds of 172.17.x.x Interface entities that clutter device/network views
- Create spurious Network entities for Docker's internal ranges
- Generate observations that look like real devices but aren't reachable

Containers on macvlan/ipvlan ARE real LAN participants and get full entity treatment.

---

## Docker Engine

Per-host Docker daemon metadata. One per managed system that has Docker installed.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent (PK, 1:1 with the host agent) |
| reachable | Whether the daemon responded at last inventory |
| version | Docker server version |
| api_version | Docker API version |
| engine_id | Docker daemon ID |
| os_type | `linux` / `windows` |
| architecture | Host architecture |
| storage_driver | overlay2, btrfs, etc. |
| cgroup_driver | cgroupfs / systemd |
| swarm_state | `active` / `inactive` / empty |
| containers_running | Count at last inventory |
| containers_total | Total container count |
| images_total | Image count |
| volumes_total | Volume count |
| networks_total | Network count |
| updated_at | When last refreshed |

There is no `record_json` blob — the per-image/volume/network detail lives in [docker_images](#docker_images), [docker_volumes](#docker_volumes), and [docker_networks_engine](#docker_networks_engine).

### docker_images

One row per image known to the engine.

| Field | Description |
|-------|-------------|
| engine_id | FK → Docker Engine (= agent_id) |
| image_id | Image content hash (sha256:...) |
| repository | Repo name (e.g., `nginx`) |
| tag | Tag (`latest`, `1.27`) |
| size_bytes | On-disk size |
| created_at | Image build time (from manifest) |
| first_seen_at | First report on this engine |
| last_seen_at | Last report |

**Key:** `(engine_id, image_id, repository, tag)` — same content hash can be tagged multiple ways.

### docker_volumes

| Field | Description |
|-------|-------------|
| engine_id | FK → Docker Engine |
| name | Volume name |
| driver | Volume driver |
| mountpoint | Host filesystem path |
| created_at | When created |
| first_seen_at | First report |
| last_seen_at | Last report |

**Key:** `(engine_id, name)`. Labels in [docker_volume_labels](#docker_volume_labels) (junction).

### docker_networks_engine

Engine-side Docker network metadata. Distinct from the LAN-level Network entity — these are Docker's internal network constructs.

| Field | Description |
|-------|-------------|
| engine_id | FK → Docker Engine |
| network_id | Docker network ID |
| name | Network name (`bridge`, `host`, `my-app_default`) |
| driver | `bridge` / `host` / `overlay` / `macvlan` / `ipvlan` / `none` |
| scope | `local` / `swarm` |
| internal | True if no external connectivity |
| attachable | True if standalone containers can attach |
| ipam_driver | IP address management driver |
| first_seen_at | First report |
| last_seen_at | Last report |

**Key:** `(engine_id, network_id)`. Subnets in `docker_network_subnets` (engine_id, network_id, cidr, gateway).

### container_mounts

One row per mount on a container System.

| Field | Description |
|-------|-------------|
| system_id | FK → System (must be type=container) |
| mount_type | `bind` / `volume` / `tmpfs` |
| name | Volume name (NULL for binds/tmpfs) |
| source | Host path or volume name |
| destination | Path inside the container |
| driver | Volume driver (NULL for binds) |
| mode | Mount mode string |
| rw | True if read-write |
| propagation | Mount propagation flag |

**Key:** `(system_id, destination)`.

### container_ports

Published port bindings. One row per host-port mapping.

| Field | Description |
|-------|-------------|
| system_id | FK → System (container) |
| host_ip | Host bind address (`0.0.0.0`, `::`, or specific) |
| host_port | Host-side port |
| container_port | Container-internal port |
| protocol | `tcp` / `udp` / `sctp` |

**Key:** `(system_id, host_ip, host_port, protocol)`.

---

## Event

An activity log entry. Records significant state changes for audit and timeline display.

| Field | Description |
|-------|-------------|
| id | Auto-increment |
| type | `agent_registered` / `agent_approved` / `agent_deregistered` / `agent_booted` / `device_discovered` / `container_added` / `container_removed` / `alert_fired` / `alert_resolved` / `config_changed` / ... |
| severity | `info` / `warn` / `error` |
| source | What generated the event (agent ID, "terrain", "system", admin username) |
| target_kind | Entity type affected: `agent` / `hardware` / `system` / `interface` / `network` / `alert` |
| target_id | ID of the affected entity |
| summary | Human-readable one-line description |
| detail_json | Optional small structured payload — bounded ≤2 KB, schema-per-type. Anything bigger or recurring becomes its own table. |
| created_at | When the event occurred |

**Reboot tracking:** boot events use `type=agent_booted` with `detail_json={kernel, boot_time}`. Cross-cycle boot_time changes are how Derive detects reboots; the corresponding boot event is the audit trail.

**Retention:** Configurable max age (default 90 days). Events are append-only, never updated.

**Not observations:** Events record *our system's reactions* (an agent registered, an alert fired). Observations record *what sources told us about the network*. Don't conflate them.

---

## Host Posture & Inventory Tables

State tables derived from agent inventory. Each section below was previously a JSON section inside the deprecated `inventory_json` blob. They share a common shape: one row per agent (for 1:1 facts) or one row per (agent, key) for collections, with `updated_at` set by Derive after each ingest.

### update_status

OS package-manager state. One row per agent.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent (PK) |
| manager | `apt` / `dnf` / `yum` / `pacman` / `apk` / `brew` / `winget` / `synopkg` / `unknown` |
| pending_count | Number of pending updates |
| security_count | Number of pending security updates |
| reboot_required | Bool |
| last_checked_at | When the agent last refreshed update metadata |
| updated_at | Last server refresh |

### pending_updates

One row per pending update. Replaces a JSON array inside the old update blob.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| name | Package name |
| current_version | Installed version (may be empty for net-new packages) |
| new_version | Available version |
| source | Repo/source name |
| security | True if security update |
| first_seen_at | When first observed pending |
| last_seen_at | Last observation |

**Key:** `(agent_id, name)`.

### security_posture

Host-level security flags. One row per agent.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent (PK) |
| tpm_present | Bool |
| tpm_version | String (`1.2`, `2.0`, NULL) |
| secure_boot | `enabled` / `disabled` / `unsupported` / `unknown` |
| selinux_mode | `enforcing` / `permissive` / `disabled` / `unknown` (NULL on non-Linux) |
| apparmor_mode | `enforcing` / `complain` / `disabled` / `unknown` (NULL on non-Linux) |
| updated_at | Last refresh |

### firewall_status

Host firewall summary. One row per agent.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent (PK) |
| provider | `ufw` / `firewalld` / `iptables` / `nftables` / `windows-defender-firewall` / `pf` / `none` |
| enabled | Bool |
| default_policy | `allow` / `deny` / `reject` / `unknown` |
| updated_at | Last refresh |

### firewall_profiles

Per-zone/per-profile firewall state.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| profile_name | `public` / `private` / `domain` / `work` / firewalld zone name / etc. |
| state | `on` / `off` |
| updated_at | Last refresh |

**Key:** `(agent_id, profile_name)`.

### antivirus_products

One row per detected AV/EDR product.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| name | Product name |
| enabled | Bool |
| realtime | Real-time protection on |
| up_to_date | Signatures current |
| signature_version | Signature DB version |
| signature_age_hours | Hours since signatures updated |
| last_scan_at | Most recent scan |
| updated_at | Last refresh |

**Key:** `(agent_id, name)`.

### encrypted_volumes

One row per detected encrypted volume (BitLocker, FileVault, LUKS, dm-crypt, APFS encryption).

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| mountpoint | Mountpoint (or volume identifier if not mounted) |
| device | Block device path |
| encryption_type | `bitlocker` / `filevault` / `luks` / `dm-crypt` / `apfs-encrypted` / `other` |
| status | `unlocked` / `locked` / `encrypting` / `decrypting` / `unknown` |
| updated_at | Last refresh |

**Key:** `(agent_id, mountpoint)`.

### failed_services

Services in a failed state at last inventory. We only persist failures, not the full service list (which lives in the agent's own service manager).

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| name | Service name (systemd unit, launchd label, Windows service name) |
| display_name | Friendly name |
| state | `failed` / `dead` / `exited` |
| sub_state | Manager-specific sub-state |
| start_mode | `auto` / `manual` / `disabled` |
| exit_code | Last exit code (when known) |
| first_seen_failed_at | When this service first appeared failed |
| last_seen_at | Last observation |

**Key:** `(agent_id, name)`. Rows are removed when the service is no longer reported as failed.

### listening_ports

Sockets in LISTEN state on the host.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| proto | `tcp` / `tcp6` / `udp` / `udp6` |
| address | Bound address (`0.0.0.0`, `::`, specific IP) |
| port | Port number |
| process_name | Owning process |
| pid | PID at last observation |
| first_seen_at | First observation |
| last_seen_at | Last observation |

**Key:** `(agent_id, proto, address, port)`.

### local_users

Local user accounts on the host.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| name | Username |
| uid | UID (NULL on Windows) |
| gid | Primary GID (NULL on Windows) |
| home_dir | Home directory |
| shell | Login shell |
| is_admin | Bool — member of admin/wheel/sudo/Administrators |
| disabled | Bool |
| last_login_at | Most recent login (when reported) |
| password_age_days | Days since last password change (when reported) |
| updated_at | Last refresh |

**Key:** `(agent_id, name)`.

### logged_in_sessions

Currently active user sessions on the host.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| user | Username |
| tty | Terminal/console (when applicable) |
| host | Remote host (NULL for local logins) |
| login_at | Session start time |
| updated_at | Last refresh |

**Key:** `(agent_id, user, tty, login_at)`.

### routes

IP routing table snapshot.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| family | `ipv4` / `ipv6` |
| destination | CIDR or `default` |
| gateway | Next-hop IP (NULL for directly connected) |
| iface | Interface name |
| metric | Route metric |
| updated_at | Last refresh |

**Key:** `(agent_id, family, destination, iface)`.

### processes_snapshot (time-series)

Top-N process samples. Time-bucketed — see [Snapshot retention](#metric-snapshot-tables) for tiers.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| ts | Sample timestamp |
| pid | Process ID at sample time |
| name | Process name |
| user | Owning user |
| cpu_pct | CPU usage |
| mem_pct | Memory usage |
| mem_bytes | RSS |
| cmd | Command line (truncated to 1 KB) |

**Reporting policy:** the agent sends the top-N by CPU and the top-N by memory per sample (N=20 default), not the full process list. Captured at the inventory cadence, not the metric cadence.

### packages

Installed packages. High-cardinality on Linux desktops (1000+ rows per agent), so retention is bounded.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| manager | `apt` / `dnf` / `pacman` / `brew` / `winget` / `synopkg` / ... |
| name | Package name |
| version | Installed version |
| arch | Package architecture |
| first_seen_at | First time observed installed |
| last_seen_at | Most recent observation |

**Key:** `(agent_id, manager, name, arch)`. Retention: rows whose `last_seen_at` is older than the agent's most recent successful package inventory cycle are deleted (the package is no longer installed). The full table is rebuilt per-agent each cycle.

### hassio_info

Home Assistant Supervisor metadata. One row per agent running Hass.io.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent (PK) |
| supervisor_version | Supervisor version |
| core_version | Home Assistant core version |
| os_version | Hass OS version |
| channel | Release channel |
| arch | Hass.io arch |
| machine | Machine model |
| host_os | Host OS string |
| host_kernel | Host kernel |
| chassis | Chassis type |
| boot_time | Last Hass boot |
| updated_at | Last refresh |

### hassio_addons

One row per installed Hass.io addon.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| slug | Addon slug |
| name | Display name |
| version | Installed version |
| state | `started` / `stopped` |
| update_available | Bool |
| first_seen_at | First observation |
| last_seen_at | Last observation |

**Key:** `(agent_id, slug)`.

---

## Metric Snapshot Tables

Time-series data collected by agents. Separate from the entity model (not entities) but defined here for completeness since they're core to the agent detail and dashboard views.

**Wire vs storage:** The schemas below describe what is *stored*. What rides on the wire per-cycle is narrower — per-disk and per-interface snapshots reference their parent entity by short stable key (`device_name`, `iface_name`) and omit identity fields (MAC, IP, mountpoint, fs_type, model, size) that the server already has from inventory. The ingest stage rehydrates the stored row by joining `(agent_id, key)` to the disk/interface entity. See `agent-lifecycle.md` → Wire Efficiency for the full reporting policy.

### metric_snapshots
| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| ts | Timestamp (RFC3339) |
| cpu_pct | CPU utilization percentage |
| mem_used_bytes | Memory in use |
| mem_total_bytes | Total physical memory |
| load_1 / load_5 / load_15 | Load averages |
| uptime_seconds | System uptime |

### disk_snapshots
| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| ts | Timestamp |
| device | Device path |
| mountpoint | Filesystem mount point |
| used_bytes | Space consumed |
| total_bytes | Total capacity |
| fs_type | Filesystem type |

### interface_snapshots
| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| ts | Timestamp |
| iface | Interface name |
| ip | IP at snapshot time |
| mac | MAC at snapshot time |
| rx_bytes / tx_bytes | Cumulative counters |
| rx_packets / tx_packets | Cumulative counters |
| is_up | Interface link state |

**Retention:** Raw snapshots → 48h, then rolled up per `metrics-and-alerting.md`.

### filesystem_snapshots

Per-mount usage over time. Replaces the deprecated `FilesystemUsage` JSON section.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| ts | Timestamp |
| mountpoint | Mount path |
| device | Block device |
| fs_type | Filesystem type |
| total_bytes | Total capacity |
| used_bytes | Used bytes |
| free_bytes | Free bytes |
| inodes_used | Used inodes (NULL when N/A) |
| inodes_free | Free inodes (NULL when N/A) |

**Rollup tiers:** same as `disk_snapshots` (raw 48h → 5min 7d → hourly 90d → daily 1y). Aggregations: `MAX(used_bytes)`, `MIN(free_bytes)`, `LAST(total_bytes)`.

### temperature_snapshots

Thermal samples from `hwmon`, `thermal_zone*`, SMART, GPU drivers, etc.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| ts | Timestamp |
| sensor_name | Sensor label (e.g., `cpu_package`, `nvme0`, `gpu0`, `thermal_zone0`) |
| sensor_type | `cpu` / `disk` / `gpu` / `chassis` / `battery` / `other` |
| celsius | Temperature reading |

**Key cardinality:** small per agent (typically 5–20 sensors). Rollup tiers as above; aggregation: `MAX(celsius)` per bucket.

### battery_snapshots

For laptops, tablets, and UPS-style integrations.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| ts | Timestamp |
| design_capacity_wh | Design capacity (watt-hours) |
| current_capacity_wh | Current full-charge capacity (watt-hours) |
| health_pct | Derived: current / design |
| cycle_count | Battery cycles |
| state | `charging` / `discharging` / `full` / `idle` / `unknown` |
| charge_pct | Current charge level |

**Rollup:** same tiers. Aggregation: `LAST(*)` per bucket (battery state isn't usefully averaged).

---

## Alert Rules

Defines a condition that, when sustained, fires an alert notification.

| Field | Description |
|-------|-------------|
| id | Auto-increment |
| name | Human label ("High CPU on production servers") |
| metric_kind | Evaluator family: `numeric_snapshot` / `posture_bool` / `posture_count` / `posture_age` / `source_health` |
| metric_path | Free-form identifier scoped by `metric_kind`. Examples: `snapshot.cpu_pct`, `snapshot.disk_pct`, `posture.firewall.enabled`, `posture.failed_services.count`, `posture.antivirus.signature_age_hours`, `source.last_success_age_seconds`. The evaluator dispatches by `metric_kind` and uses `metric_path` to pick the column / table / aggregation. |
| operator | `>` / `<` / `>=` / `<=` / `==` / `!=` |
| threshold | Comparison value. Numeric for `numeric_snapshot`/`posture_count`/`posture_age`/`source_health`; boolean (`true`/`false`) for `posture_bool` (operator is ignored, equality is implied). |
| duration_seconds | How long condition must be sustained before firing. Also acts as the anti-flap window on resolve — see `metrics-and-alerting.md` → Anti-Flap. |
| target_kind | Scope: `agent` / `tag` / `source` / `service` / `disk` / `network` / `hardware` / `all` |
| target_id | ID of the targeted entity, tag name, or empty for `all`. Must resolve under `target_kind`. |
| severity | `info` / `warning` / `critical` |
| channel_id | FK → Notification Channel |
| enabled | Boolean toggle |
| created_at | When created |

**Why this shape.** The old fixed `metric` enum (`cpu_pct | mem_pct | disk_pct | offline_minutes`) couldn't represent the posture, source-health, and per-entity rules the rest of the entity model already supports. `metric_kind` selects an evaluator (which knows what to query and how to aggregate); `metric_path` is the within-kind identifier. Adding a new evaluator family is a code change in one place (the evaluator dispatch table); adding a new path within an existing family requires no code change. The Foundation Critic verifies that every documented path resolves to a real column or aggregation.

---

## Alert Firings

One row per active or resolved alert instance.

| Field | Description |
|-------|-------------|
| id | Auto-increment |
| rule_id | FK → Alert Rule |
| agent_id | Which agent triggered |
| started_at | When condition first sustained |
| resolved_at | When condition cleared (NULL = still firing) |
| notified | Whether notification was delivered |
| notification_error | Error message if delivery failed |
| flapping | Set when this firing opened within `5 × duration_seconds` of the rule's previous resolve for the same target |

---

## Notification Channels

Delivery targets for alert notifications.

| Field | Description |
|-------|-------------|
| id | Auto-increment |
| name | Human label ("Ops Email", "Discord Alerts") |
| kind | `email` / `webhook` (future: discord, pushover, gotify, slack) |
| config_json | Kind-specific settings (SMTP host/port/creds, webhook URL/headers/template). Secret fields inside this blob are encrypted at rest with the server DEK — same mechanism as `sources.config_json`. See infrastructure.md → Secret Storage. |
| rate_limit_per_hour | Maximum notifications this channel will emit per rolling 60-minute window. Excess notifications are coalesced into a single "N alerts suppressed" summary at the end of the window. NULL = unlimited. |
| enabled | Boolean toggle |
| created_at | When created |

---

## Maintenance Windows

A scheduled period during which matching alert rules do not fire. Used when an operator is patching a NAS, rebooting a switch, or otherwise expects state to be temporarily abnormal.

| Field | Description |
|-------|-------------|
| id | Auto-increment |
| name | Human label ("Saturday NAS reboot") |
| scope_kind | Same enum as `alert_rules.target_kind` (`agent` / `tag` / `source` / `service` / `disk` / `network` / `hardware` / `all`) |
| scope_id | ID of the entity in scope, tag name, or empty for `all` |
| starts_at | Window start (UTC) |
| ends_at | Window end (UTC). Hard cap: 7 days. Longer suppressions should be a rule disable, not a maintenance window. |
| reason | Free-text note shown in the events log when the window opens/closes |
| created_by | FK → Users |
| created_at | When created |

**Semantics:** During an active window, the alert evaluator still computes condition state but does **not** open new firings whose target falls within the window's scope. Already-open firings are not auto-resolved. When the window ends, normal evaluation resumes on the next cycle. Events are written for window-open and window-close so the audit trail is intact.

---

## Users

Admin accounts for the server UI. Not domain entities — operational data.

| Field | Description |
|-------|-------------|
| id | Auto-increment |
| username | Unique login name |
| password_hash | bcrypt hash (cost 12) |
| created_at | When account was created |
| last_login_at | Most recent successful authentication |

**Bootstrap:** First user is created via the bootstrap token flow (see infrastructure.md). No default accounts.

---

## Sessions

Server-side session tokens for authenticated admin access.

| Field | Description |
|-------|-------------|
| token | 32-byte random hex string (PK) |
| user_id | FK → Users |
| created_at | When session was established |
| expires_at | When session becomes invalid |
| last_active_at | Most recent request using this session |

**Lifecycle:** Created on login, extended on each request (sliding expiry), destroyed on logout, purged by cleanup job when expired. Stored server-side — the cookie contains only the token, never user state.

---

## Tags

Cross-entity labels. Can target any entity type.

| Field | Description |
|-------|-------------|
| target_kind | `hardware` / `system` / `interface` / `network` / `service` / `source` |
| target_id | ID of the tagged entity (string — varies by kind) |
| tag | Normalized label (lowercase, alphanumeric + `-_./:`) |
| created_at | When applied |
| created_by | Username, or `system` for derived tags |

**Primary key:** `(target_kind, target_id, tag)` — composite PK, no auto-increment. A tag is uniquely identified by what it's on and what it says.

**Aggregation:** When displaying a hardware entity, tags from all its child systems and interfaces are aggregated for filtering. A tag on any child surfaces on the parent in list views.

**Hardcoded `target_kind` enum:** the kinds list above is a closed enum, enforced at insert time. Adding a new taggable entity type requires a code change in the Tags writer, the aggregation queries, and the UI tag picker. See [Known Scaling Limits](#known-scaling-limits) — this trade-off is acceptable at current entity counts.

---

## Subsystems

Registry of collection subsystems an agent can run. Reference data — small, slow-changing, server-managed.

| Field | Description |
|-------|-------------|
| name | Stable identifier (`discovery` / `inventory` / `metrics` / `network-sensor` / `container-cache` / future) |
| description | Human description |
| expected_cadence_seconds | How often the server expects samples from this subsystem (NULL = push-driven) |
| introduced_in_version | Server version that first knew about it |
| deprecated_in_version | NULL until retired |

**Key:** `name`.

### agent_subsystems

Junction: which subsystems each agent has enabled.

| Field | Description |
|-------|-------------|
| agent_id | FK → Agent |
| subsystem_name | FK → Subsystems |
| enabled | Bool — set by agent self-report during handshake |
| last_sample_received_at | Server-tracked; updated on each ingest of that subsystem's data |
| first_seen_at | When this agent first reported having this subsystem |

**Key:** `(agent_id, subsystem_name)`.

**Replaces** the deprecated `Agent.enabled_subsystems` JSON array. See `agent-lifecycle.md` → Subsystem Registry Handshake.

---

## Junction / Association Tables

Complete list — every many-to-many or extension table referenced elsewhere in this document.

| Table | Relationship | Key |
|-------|-------------|-----|
| interface_addresses | Interface → IP addresses | `(interface_id, address)` |
| interface_networks | Interface ↔ Network membership (derived from addresses) | `(interface_id, network_id)` |
| interface_mdns_services | Interface → mDNS services advertised | `(interface_id, service_type, port)` |
| interface_mdns_txt | Interface → mDNS TXT records | `(interface_id, service_type, key)` |
| interface_tls_sans | Interface → TLS SANs per port | `(interface_id, port, san)` |
| interface_profile | Interface → structured probe results (1:1) | `(interface_id)` |
| hostname_aliases | Interface → observed names | `(interface_id, name, source)` |
| disk_partitions | Disk → partitions | `(disk_id, partition_index)` |
| disk_smart_attributes | Disk → SMART attributes | `(disk_id, attribute_id)` |
| container_metadata | System (container) → Docker-specific fields (1:1) | `(system_id)` |
| container_mounts | System (container) → mounts | `(system_id, destination)` |
| container_ports | System (container) → published port bindings | `(system_id, host_ip, host_port, protocol)` |
| system_labels | System → labels (Docker, compose, user) | `(system_id, key)` |
| system_services | System → Services it runs | embedded in `services.system_id` FK |
| hardware_systems | Hardware → Systems it hosts | embedded in `systems.hardware_id` FK |
| hardware_cpu | Hardware → CPU facts (1:1) | `(hardware_id)` |
| hardware_memory | Hardware → RAM facts (1:1) | `(hardware_id)` |
| hardware_board | Hardware → board facts (1:1) | `(hardware_id)` |
| hardware_bios | Hardware → BIOS facts (1:1) | `(hardware_id)` |
| hardware_gpu | Hardware → GPUs | `(hardware_id, slot_index)` |
| pending_updates | Agent → pending package updates | `(agent_id, name)` |
| firewall_profiles | Agent → firewall zones/profiles | `(agent_id, profile_name)` |
| antivirus_products | Agent → AV/EDR products | `(agent_id, name)` |
| encrypted_volumes | Agent → encrypted mounts | `(agent_id, mountpoint)` |
| failed_services | Agent → services in failed state | `(agent_id, name)` |
| listening_ports | Agent → listening sockets | `(agent_id, proto, address, port)` |
| local_users | Agent → local accounts | `(agent_id, name)` |
| logged_in_sessions | Agent → live login sessions | `(agent_id, user, tty, login_at)` |
| routes | Agent → routing table | `(agent_id, family, destination, iface)` |
| packages | Agent → installed packages | `(agent_id, manager, name, arch)` |
| hassio_addons | Agent → Hass.io addons | `(agent_id, slug)` |
| docker_images | Engine → images | `(engine_id, image_id, repository, tag)` |
| docker_volumes | Engine → volumes | `(engine_id, name)` |
| docker_volume_labels | docker_volumes → labels | `(engine_id, name, key)` |
| docker_networks_engine | Engine → Docker networks | `(engine_id, network_id)` |
| docker_network_subnets | docker_networks_engine → subnets | `(engine_id, network_id, cidr)` |
| agent_subsystems | Agent → enabled subsystems | `(agent_id, subsystem_name)` |
| agent_inventory_receipts | Agent → ingest audit (append-only) | `(id)` auto-increment |
| agent_unknown_sections | Agent → unmodeled payload sections (tripwire) | `(agent_id, section_name)` |
| maintenance_windows | Scheduled alert suppression by scope | `(id)` auto-increment |

**Rule:** any new entity introduced to this document MUST be listed here. The Foundation Critic should diff this table against `## ###` section headers and flag missing entries.

---

## Known Scaling Limits

These are accepted trade-offs at the current scale of the product. None of them prevents shipping; all should be re-evaluated if the entity count grows substantially.

1. **Hardcoded `target_kind` enums.** `Tags.target_kind`, `Events.target_kind`, `alert_rules.target_kind`, and `maintenance_windows.scope_kind` all use closed enum strings. Adding a new taggable/eventable/alertable/silenceable entity type requires changes in the writer, the query layer, and the UI. This is acceptable at the current entity count.

    **Migration path when this becomes painful.** When the enum hits ~10 kinds or a third cross-kind query is needed:

    1. Introduce a `nodes` table — `(node_id PK, kind TEXT, entity_id TEXT, created_at)` — and backfill one row per existing Hardware / System / Interface / Network / Service / Source / Disk / Agent / etc.
    2. Add `node_id` columns alongside the existing `target_kind` + `target_id` on Tags, Events, alert_rules, maintenance_windows, alert_firings. Backfill via the `nodes` lookup.
    3. Switch writers to populate the new `node_id` column. Leave the old `(target_kind, target_id)` columns in place as derived/legacy for one release.
    4. Switch readers to JOIN through `nodes`. Drop the old columns in a later migration.
    5. New entity types now require only a `nodes.kind` value and a writer that inserts the lookup row — no per-table enum edits.

    The migration is deliberately incremental: nothing breaks mid-flight, and each step is independently reversible.
2. **Per-agent inventory rebuild.** Tables like `packages` are rebuilt per ingest cycle (delete-then-insert per agent). This is acceptable while package counts stay in the low thousands per host. If desktop Linux agents push us past ~10,000 packages per host, switch to a delta-only ingest.
3. **Snapshot raw retention is 48h.** Operators who want longer raw retention must explicitly raise it; the default trades fidelity for disk footprint per the policy in `metrics-and-alerting.md`.
4. **`agent_unknown_sections` is not a long-term store.** Anything appearing there must be promoted to a real table within one release cycle. The Foundation Critic flags older rows.

---

## Identity Resolution Summary

| Entity | Definitive key | Heuristic key | Fallback |
|--------|---------------|---------------|----------|
| Network | gateway_mac | — | CIDR+SSID combo |
| Hardware | agent_id → serial+vendor | SSH key, mDNS hostname correlation | 1:1 with interface |
| System | agent_id (bare-metal), container_id (containers) | hostname + OS fingerprint | 1:1 with hardware |
| Interface | MAC address | — | — (MAC is always known) |
| Service | system_id + port + protocol | system_id + name | — |
| Source | configured id (operator-assigned) | — | — |
| Disk | serial number | system_id + device_path | — |
