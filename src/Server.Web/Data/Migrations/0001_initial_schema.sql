-- ── Schema ───────────────────────────────────────────────────────────────────
CREATE SCHEMA if NOT EXISTS jmwdiscovery;
SET
search_path TO jmwdiscovery, PUBLIC;

-- ── Service identity ─────────────────────────────────────────────────────────
--
-- A service_id is a stable UUID assigned to a logical service entity (DNS server,
-- monitoring platform, etc.) regardless of which physical host runs it.
-- Fingerprints are logical properties: DNS zones, DHCP subnets, server names.
--
-- Matching policy: ANY fingerprint match within the same type = same service.
-- This is more lenient than device matching because services gain/lose zones
-- over time; we don't want a zone addition to create a duplicate service record.

CREATE TABLE if NOT EXISTS services (
    id
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,  -- stable UUID as text
    TYPE
    TEXT
    NOT
    NULL, -- e.g. "technitium-dns"
    created_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );

CREATE TABLE if NOT EXISTS service_fingerprints (
    service_id
    TEXT
    NOT
    NULL
    REFERENCES
    services(
    id
            ),
    fp_type TEXT NOT NULL, -- e.g. "primary-zone", "dhcp-subnet"
    fp_value TEXT NOT NULL, -- e.g. "home.lan", "192.168.1.0/24"
    PRIMARY KEY (service_id,
                 fp_type,
                 fp_value
                )
    );

-- Lookup index: find service_id by fingerprint
CREATE INDEX if NOT EXISTS service_fingerprints_lookup_idx
    ON service_fingerprints (fp_type, fp_value);


-- ── Device identity ──────────────────────────────────────────────────────────
--
-- A device_id is a stable UUID assigned on first contact. It never changes for
-- the lifetime of a physical device, even if its IP, hostname, or MACs change.
-- Fingerprints map observed identifiers to device_ids. The primary key on
-- device_fingerprints prevents the same fingerprint from being assigned to two
-- devices — this is the uniqueness guarantee the resolution logic depends on.

CREATE TABLE if NOT EXISTS devices (
    device_id
    UUID
    NOT
    NULL
    DEFAULT
    gen_random_uuid(
                   ) PRIMARY KEY,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now (
                                                )
    );

CREATE TABLE if NOT EXISTS device_fingerprints (
    fp_type
    TEXT
    NOT
    NULL,
    fp_value
    TEXT
    NOT
    NULL, -- always normalized by FingerprintNormalizer
    device_id
    UUID
    NOT
    NULL
    REFERENCES
    devices(
    device_id
           ),
    first_seen TIMESTAMPTZ NOT NULL DEFAULT now (
                                                ),
    PRIMARY KEY (fp_type,
                 fp_value
                ) -- one device per fingerprint, enforced
    );

CREATE INDEX if NOT EXISTS device_fingerprints_device_idx
    ON device_fingerprints (device_id);


-- ── Facts history ─────────────────────────────────────────────────────────────
--
-- Append-only change log. One row per observed value change per fact ID.
-- This is the only fact table. Projections serve as the current-state view.
--
-- Dedup responsibility:
--   Primary : collectors track last-sent values and only transmit changes.
--   Secondary: the server's entity state cache (in GenericProjection) guards
--              projection writes. History writes rely on collector dedup.
--   Guard   : ON CONFLICT (id, collected_at) DO NOTHING prevents exact-duplicate
--             rows from redundant collectors whose clocks are in sync.
--
-- Two columns replace the old entity_path + attribute split:
--
--   attribute_path : structural path with NO keys — "Device.Interface.Speed"
--                    answers "what shape is this fact?"
--                    enables: WHERE attribute_path = 'Device.Interface.Speed'
--                             WHERE attribute_path LIKE 'Device.Interface.%'
--
--   key_values     : JSONB object of dimension name → key — {"Device":"r1","Interface":"eth0"}
--                    answers "which specific instance?"
--                    enables: WHERE key_values->>'Interface' = 'eth0'
--                             WHERE key_values @> '{"Device":"r1"}'::jsonb
--
-- Together they separate the "kind of thing" from the "which one", so you can
-- efficiently query across either axis without LIKE scans on embedded key strings.
--
-- The full id is kept for human readability, dedup, and as a stable primary key.
--
-- Value storage:
--   value_str    : String, IPv4, IPv6, IPPrefix, MacAddress (human-readable)
--   value_long   : Long, Bool (1/0), DateTimeOffset (UTC ticks), TimeSpan (ticks)
--   value_double : Double

CREATE TABLE if NOT EXISTS facts_history (
    id
    TEXT
    NOT
    NULL,
    attribute_path
    TEXT
    NOT
    NULL, -- "Device.Interface.Speed" — no keys
    key_values
    JSONB
    NOT
    NULL, -- {"Device":"r1","Interface":"eth0"}
    kind
    SMALLINT
    NOT
    NULL,
    value_str
    TEXT,
    value_long
    BIGINT,
    value_double
    DOUBLE
    PRECISION,
    collected_at
    TIMESTAMPTZ
    NOT
    NULL,
    PRIMARY
    KEY (
    id,
    collected_at
        )
    );

-- Dedup CTE: latest value per ID — index-only with INCLUDE columns.
CREATE INDEX if NOT EXISTS facts_history_id_time_idx ON facts_history
    (id, collected_at DESC)
    INCLUDE (kind, value_str, value_long, value_double);

-- Structural path queries: "all Speed history", "all Interface.* history"
CREATE INDEX if NOT EXISTS facts_history_path_time_idx ON facts_history
    (attribute_path text_pattern_ops, collected_at DESC);

-- Key-value queries: "all facts for eth0", "all facts for router-1"
-- @> containment and ->>'key' expression both use this index.
CREATE INDEX if NOT EXISTS facts_history_keys_gin ON facts_history USING GIN (key_values);

-- Time-range sweep: "what changed in the last hour"
CREATE INDEX if NOT EXISTS facts_history_time_idx ON facts_history (collected_at DESC);

-- Partition by month in production:
--   PARTITION BY RANGE (collected_at)
-- Enables cheap DROP of old partitions instead of DELETE + vacuum.


-- ═════════════════════════════════════════════════════════════════════════════
-- PROJECTION TABLES
--
-- Current state views. GenericProjection generates SQL that targets these tables.
--
-- Column naming convention for PRIMARY KEY columns:
--   GenericProjection lowercases the dimension name directly:
--     "Device"       → device
--     "Interface"    → interface
--     "ARP"          → arp
--     "TrustedCA"    → trustedca
--     "ListeningPort"→ listeningport
--   Do NOT use a "dim_" prefix — it will not match.
-- ═════════════════════════════════════════════════════════════════════════════


-- ── Per-device singleton projections ─────────────────────────────────────────
-- One row per device. GenericProjection uses ON CONFLICT (device) DO UPDATE.


-- proj_devices: device type/vendor identity from sighting facts.
CREATE TABLE if NOT EXISTS proj_devices (
    device
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    vendor
    TEXT,
    kind
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );


-- proj_systems: OS info and real-time system metrics.
CREATE TABLE if NOT EXISTS proj_systems (
    device
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    hostname
    TEXT,
    os_family
    TEXT,
    os_distro
    TEXT,
    os_version
    TEXT,
    os_build
    TEXT,
    kernel
    TEXT,
    kernel_arch
    TEXT,
    timezone
    TEXT,
    boot_time
    TIMESTAMPTZ,
    uptime_seconds
    BIGINT,
    cpu_percent
    DOUBLE
    PRECISION,
    mem_used_bytes
    BIGINT,
    mem_total_bytes
    BIGINT,
    mem_used_pct
    DOUBLE
    PRECISION,
    load_1
    DOUBLE
    PRECISION,
    load_5
    DOUBLE
    PRECISION,
    load_15
    DOUBLE
    PRECISION,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );


-- proj_hardware: CPU, RAM, board, system info, BIOS, virtualization.
CREATE TABLE if NOT EXISTS proj_hardware (
    device
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    cpu_model
    TEXT,
    cpu_vendor
    TEXT,
    cpu_cores
    BIGINT,
    cpu_logical_cores
    BIGINT,
    cpu_mhz
    DOUBLE
    PRECISION,
    total_mem_bytes
    BIGINT,
    system_vendor
    TEXT,
    system_model
    TEXT,
    system_serial
    TEXT,
    bios_version
    TEXT,
    virtualization
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );


-- proj_hardware_inventory: component-level hardware inventory.
--
-- One row per (device, component). Covers all component types from all sources:
--   Local agents:    dmidecode (DIMMs, CPU sockets, slots), lspci (PCIe devices),
--                    IPMI/sensors (fans, PSUs, temp sensors)
--   Network devices: SNMP ENTITY-MIB entPhysicalTable (line cards, supervisor
--                    modules, transceivers, fans, PSUs) via SSH/SNMP collectors
--
-- Component key is a stable identifier scoped to the device:
--   dmidecode:   handle string ("0x0038")
--   lspci:       PCI bus address ("0000:03:00.0")
--   IPMI:        sensor name ("Fan1", "PSU1 Status")
--   ENTITY-MIB:  entPhysicalIndex as string ("1003")
--   CLI slot:    normalized slot path ("chassis/2/module/1")
--
-- class values: cpu | memory | storage | fan | psu | transceiver |
--               nic | module | chassis | sensor | port | other
-- status values: ok | failed | absent | unknown
-- is_fru: true if field-replaceable (meaningful for asset tracking and alerting)
--
-- details JSONB: type-specific attributes. Examples:
--   memory:      {"size_bytes":17179869184,"speed_mhz":3200,"memory_type":"DDR4","form_factor":"DIMM"}
--   fan:         {"rpm":2400,"rpm_low_threshold":800}
--   psu:         {"capacity_watts":750,"input_voltage":120.0}
--   transceiver: {"wavelength_nm":1310,"tx_power_dbm":-2.5,"rx_power_dbm":-3.1,"temperature_c":42.1}
--   module:      {"port_count":48}
--   cpu:         {"cores":8,"threads":16,"speed_mhz":3600,"socket":"LGA1700"}

CREATE TABLE if NOT EXISTS proj_hardware_inventory (
    device
    TEXT
    NOT
    NULL,
    hwcomponent
    TEXT
    NOT
    NULL, -- stable component key (see above)
    class
    TEXT, -- component type discriminator
    slot
    TEXT, -- human-readable position
    description
    TEXT,
    vendor
    TEXT,
    model
    TEXT,
    serial
    TEXT,
    firmware
    TEXT,
    status
    TEXT,
    is_fru
    BOOLEAN,
    details
    JSONB,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 hwcomponent
                )
    );

-- "Show all components with a non-OK status across the network."
CREATE INDEX if NOT EXISTS proj_hardware_inventory_status_idx
    ON proj_hardware_inventory (status)
    WHERE status IS NOT NULL;

-- "Show all transceivers / all fans / etc. across the network."
CREATE INDEX if NOT EXISTS proj_hardware_inventory_class_idx
    ON proj_hardware_inventory (class)
    WHERE class IS NOT NULL;

-- Asset tracking: find a component by serial number across all devices.
CREATE INDEX if NOT EXISTS proj_hardware_inventory_serial_idx
    ON proj_hardware_inventory (serial)
    WHERE serial IS NOT NULL;


-- ── Typed views over proj_hardware_inventory ──────────────────────────────────
-- Each view filters by class and projects the relevant details JSONB fields as
-- typed columns. No storage overhead; views update automatically with the table.

CREATE
OR REPLACE VIEW proj_hw_memory AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , status
  , is_fru
  , (details ->>'size_bytes')::bigint   AS size_bytes, (details ->>'speed_mhz')::int       AS speed_mhz, (details ->>'memory_type') AS memory_type
  , (details ->>'form_factor') AS form_factor
  , updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'memory';

CREATE
OR REPLACE VIEW proj_hw_cpus AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , status
  , (details ->>'cores')::int           AS cores, (details ->>'threads')::int         AS threads, (details ->>'speed_mhz')::int       AS speed_mhz, (details ->>'socket') AS socket
  , updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'cpu';

CREATE
OR REPLACE VIEW proj_hw_fans AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , status
  , is_fru
  , (details ->>'rpm')::int               AS rpm, (details ->>'rpm_low_threshold')::int  AS rpm_low_threshold, updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'fan';

CREATE
OR REPLACE VIEW proj_hw_psus AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , status
  , is_fru
  , (details ->>'capacity_watts')::int     AS capacity_watts, (details ->>'input_voltage')::numeric  AS input_voltage, updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'psu';

CREATE
OR REPLACE VIEW proj_hw_transceivers AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , status
  , is_fru
  , (details ->>'wavelength_nm')::int       AS wavelength_nm, (details ->>'tx_power_dbm')::numeric    AS tx_power_dbm, (details ->>'rx_power_dbm')::numeric    AS rx_power_dbm, (details ->>'temperature_c')::numeric   AS temperature_c, updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'transceiver';

CREATE
OR REPLACE VIEW proj_hw_modules AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , firmware
  , status
  , is_fru
  , (details ->>'port_count')::int          AS port_count, updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'module';

CREATE
OR REPLACE VIEW proj_hw_storage AS
SELECT
    device
  , hwcomponent
  , slot
  , description
  , vendor
  , model
  , serial
  , firmware
  , status
  , is_fru
  , (details ->>'size_bytes')::bigint        AS size_bytes, (details ->>'type') AS type
  , (details ->>'smart_health') AS smart_health
  , (details ->>'smart_temp_c')::numeric     AS smart_temp_c, (details ->>'smart_wear_pct')::numeric   AS smart_wear_pct, updated_at
FROM
    proj_hardware_inventory
WHERE
    class = 'storage';


-- proj_security: firewall, AV, secure boot, TPM, SELinux posture.
CREATE TABLE if NOT EXISTS proj_security (
    device
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    firewall_enabled
    BOOLEAN,
    firewall_provider
    TEXT,
    av_name
    TEXT,
    av_enabled
    BOOLEAN,
    av_up_to_date
    BOOLEAN,
    secure_boot
    BOOLEAN,
    tpm_present
    BOOLEAN,
    tpm_version
    TEXT,
    selinux_mode
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );


-- proj_batteries: battery capacity, cycle count, and charge state.
CREATE TABLE if NOT EXISTS proj_batteries (
    device
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    design_capacity_wh
    DOUBLE
    PRECISION,
    current_capacity_wh
    DOUBLE
    PRECISION,
    cycle_count
    BIGINT,
    STATE
    TEXT,
    charge_pct
    DOUBLE
    PRECISION,
    health_pct
    DOUBLE
    PRECISION,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );


-- proj_updates: pending system package updates.
CREATE TABLE if NOT EXISTS proj_updates (
    device
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    manager
    TEXT,
    pending
    BIGINT,
    SECURITY
    BIGINT,
    reboot_required
    BOOLEAN,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );


-- proj_docker: Docker engine summary (container counts, version).
CREATE TABLE if NOT EXISTS proj_docker (
    device
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    version
    TEXT,
    api_version
    TEXT,
    storage_driver
    TEXT,
    containers_running
    BIGINT,
    containers_paused
    BIGINT,
    containers_stopped
    BIGINT,
    images
    BIGINT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );


-- ── Per-device list projections ───────────────────────────────────────────────
-- One row per (device, key). GenericProjection ON CONFLICT (device, <key>) DO UPDATE.


-- proj_interfaces: network interface config and traffic counters.
-- Key = MAC address (normalized: 12 lowercase hex chars, no separators).
CREATE TABLE if NOT EXISTS proj_interfaces (
    device
    TEXT
    NOT
    NULL,
    interface
    TEXT
    NOT
    NULL, -- MAC address
    NAME
    TEXT,
    mtu
    BIGINT,
    up
    BOOLEAN,
    loopback
    BOOLEAN,
    speed_bps
    BIGINT,
    duplex
    TEXT,
    TYPE
    TEXT,
    rx_bytes
    BIGINT,
    tx_bytes
    BIGINT,
    rx_packets
    BIGINT,
    tx_packets
    BIGINT,
    total_bytes
    BIGINT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 interface
                )
    );


-- proj_disks: physical disk inventory and SMART health data.
-- Key = serial number (or disk name when serial is not available).
CREATE TABLE if NOT EXISTS proj_disks (
    device
    TEXT
    NOT
    NULL,
    disk
    TEXT
    NOT
    NULL,
    NAME
    TEXT,
    model
    TEXT,
    size_bytes
    BIGINT,
    TYPE
    TEXT,
    smart_health
    TEXT,
    smart_temp_c
    DOUBLE
    PRECISION,
    smart_power_on_hours
    BIGINT,
    smart_power_cycles
    BIGINT,
    smart_reallocated_sectors
    BIGINT,
    smart_uncorrectable_errors
    BIGINT,
    smart_wear_pct
    DOUBLE
    PRECISION,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 disk
                )
    );

-- Quickly find devices with degraded disks.
CREATE INDEX if NOT EXISTS proj_disks_health_idx
    ON proj_disks (smart_health)
    WHERE smart_health IS NOT NULL;


-- proj_filesystems: mount points with capacity and usage.
-- Key = mountpoint path (e.g. "/", "/home", "C:\").
CREATE TABLE if NOT EXISTS proj_filesystems (
    device
    TEXT
    NOT
    NULL,
    filesystem
    TEXT
    NOT
    NULL, -- mountpoint path
    fs_type
    TEXT,
    total_bytes
    BIGINT,
    used_bytes
    BIGINT,
    free_bytes
    BIGINT,
    used_pct
    DOUBLE
    PRECISION,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 filesystem
                )
    );


-- proj_containers: Docker containers (running and stopped).
-- Key = short container ID (first 12 chars of the 64-char full ID).
CREATE TABLE if NOT EXISTS proj_containers (
    device
    TEXT
    NOT
    NULL,
    container
    TEXT
    NOT
    NULL,
    NAME
    TEXT,
    image
    TEXT,
    STATE
    TEXT,
    health
    TEXT,
    cpu_pct
    DOUBLE
    PRECISION,
    mem_usage_bytes
    BIGINT,
    net_rx_bytes
    BIGINT,
    net_tx_bytes
    BIGINT,
    restart_count
    BIGINT,
    compose_project
    TEXT,
    compose_service
    TEXT,
    restart_policy
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 container
                )
    );

-- "Show me all containers that are not in the 'running' state."
CREATE INDEX if NOT EXISTS proj_containers_state_idx
    ON proj_containers (STATE)
    WHERE STATE IS NOT NULL;


-- proj_processes: top-25 processes by cumulative CPU time per collection cycle.
-- Key = PID (as string). PIDs are reused across reboots but accurate within a snapshot.
CREATE TABLE if NOT EXISTS proj_processes (
    device
    TEXT
    NOT
    NULL,
    process
    TEXT
    NOT
    NULL, -- PID as string
    NAME
    TEXT,
    cpu_time_secs
    DOUBLE
    PRECISION,
    mem_bytes
    BIGINT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 process
                )
    );

-- "Which devices are running process X?"
CREATE INDEX if NOT EXISTS proj_processes_name_idx
    ON proj_processes (NAME)
    WHERE NAME IS NOT NULL;


-- proj_ports: listening TCP/UDP endpoints.
-- Key = "proto:addr:port" composite (e.g. "tcp:0.0.0.0:22", "udp6:::53").
CREATE TABLE if NOT EXISTS proj_ports (
    device
    TEXT
    NOT
    NULL,
    listeningport
    TEXT
    NOT
    NULL,
    protocol
    TEXT,
    address
    TEXT,
    port
    INTEGER,
    process_name
    TEXT,
    pid
    BIGINT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 listeningport
                )
    );

-- "What is listening on port 443 across the network?"
CREATE INDEX if NOT EXISTS proj_ports_port_idx
    ON proj_ports (port)
    WHERE port IS NOT NULL;


-- proj_device_services: failed or degraded system services only.
-- Key = service/unit name. Healthy services do not appear here.
-- Linux: systemd units in failed state.
-- macOS: launchctl jobs with non-zero last exit.
-- Windows: auto-start services that are not running.
CREATE TABLE if NOT EXISTS proj_device_services (
    device
    TEXT
    NOT
    NULL,
    service
    TEXT
    NOT
    NULL, -- unit/service name
    NAME
    TEXT,
    active_state
    TEXT,
    sub_state
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 service
                )
    );


-- proj_local_users: local user accounts.
-- Key = username. Root (UID 0) and human accounts (UID >= 1000) are included;
-- system accounts are skipped. UID is stored as text to accommodate Windows SIDs.
CREATE TABLE if NOT EXISTS proj_local_users (
    device
    TEXT
    NOT
    NULL,
    localuser
    TEXT
    NOT
    NULL, -- username
    username
    TEXT,
    uid
    TEXT, -- numeric string (Linux/macOS) or SID (Windows)
    gid
    TEXT,
    home
    TEXT,
    shell
    TEXT,
    is_admin
    BOOLEAN,
    disabled
    BOOLEAN,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 localuser
                )
    );

-- "Show all admin accounts across the network."
CREATE INDEX if NOT EXISTS proj_local_users_admin_idx
    ON proj_local_users (is_admin)
    WHERE is_admin IS NOT NULL;


-- proj_sessions: active interactive login sessions.
-- Key = "user@tty" (e.g. "jason@pts/0", "Administrator@RDP-Tcp#0").
-- Transient — rows appear when a session is open and go stale when it closes.
CREATE TABLE if NOT EXISTS proj_sessions (
    device
    TEXT
    NOT
    NULL,
    SESSION
    TEXT
    NOT
    NULL, -- "user@tty"
    username
    TEXT,
    tty
    TEXT,
    login_at
    TEXT, -- text; format varies by OS
    host
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 SESSION
                )
    );


-- ── Routing / ARP / Certs ─────────────────────────────────────────────────────


-- proj_device_routes: IPv4/IPv6 routing table entries.
-- Key = destination CIDR (e.g. "0.0.0.0/0", "192.168.1.0/24", "::/0").
CREATE TABLE if NOT EXISTS proj_device_routes (
    device
    TEXT
    NOT
    NULL,
    route
    TEXT
    NOT
    NULL, -- destination CIDR
    family
    TEXT,
    gateway
    TEXT,
    iface
    TEXT,
    metric
    INTEGER,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 route
                )
    );


-- proj_device_arp: ARP / neighbor cache entries.
-- Key = neighbor IP address.
-- Index on mac enables IP→device correlation from DHCP lease data.
CREATE TABLE if NOT EXISTS proj_device_arp (
    device
    TEXT
    NOT
    NULL,
    arp
    TEXT
    NOT
    NULL, -- neighbor IP address
    mac
    TEXT,
    iface
    TEXT,
    STATE
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 arp
                )
    );

CREATE INDEX if NOT EXISTS proj_device_arp_mac_idx
    ON proj_device_arp (mac)
    WHERE mac IS NOT NULL;


-- proj_device_certs: X.509 certificates found on the device by CertScanCollector.
-- Key = SHA-256 fingerprint (lowercase hex, no colons). Deduplicates identical certs
-- across multiple paths. Correlate with proj_service_ca.root_fingerprint to identify
-- which certs were issued by a known CA.
CREATE TABLE if NOT EXISTS proj_device_certs (
    device
    TEXT
    NOT
    NULL,
    cert
    TEXT
    NOT
    NULL, -- SHA-256 fingerprint (lowercase hex)
    subject_dn
    TEXT,
    issuer_dn
    TEXT,
    not_before
    TIMESTAMPTZ,
    not_after
    TIMESTAMPTZ,
    PATH
    TEXT,
    is_ca
    BOOLEAN,
    sans
    TEXT, -- comma-joined DNS SANs (up to 10)
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 cert
                )
    );

-- Expiry monitoring: "certs expiring in the next 30 days."
CREATE INDEX if NOT EXISTS proj_device_certs_not_after_idx
    ON proj_device_certs (not_after)
    WHERE not_after IS NOT NULL;

-- CA attribution: "which devices hold a cert issued by CA X?"
CREATE INDEX if NOT EXISTS proj_device_certs_issuer_idx
    ON proj_device_certs (issuer_dn)
    WHERE issuer_dn IS NOT NULL;


-- proj_device_trusted_cas: step-ca client trust configuration (defaults.json).
-- Key = root CA fingerprint. Correlates with proj_service_ca.root_fingerprint
-- to map enrollment → CA service instance.
CREATE TABLE if NOT EXISTS proj_device_trusted_cas (
    device
    TEXT
    NOT
    NULL,
    trustedca
    TEXT
    NOT
    NULL, -- root CA fingerprint
    ca_url
    TEXT,
    root_path
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 trustedca
                )
    );


-- ── Service projections ───────────────────────────────────────────────────────


-- proj_services: current identity record for each logical service.
-- One row per stable service ID. Service dimension key = stable service ID.
CREATE TABLE if NOT EXISTS proj_services (
    service
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    service_id
    TEXT,
    TYPE
    TEXT,
    device_id
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );


-- proj_service_ca: CA root/intermediate cert expiry and status.
CREATE TABLE if NOT EXISTS proj_service_ca (
    service
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    ca_status
    TEXT,
    ca_address
    TEXT,
    root_subject_dn
    TEXT,
    root_not_before
    TIMESTAMPTZ,
    root_not_after
    TIMESTAMPTZ,
    root_fingerprint
    TEXT,
    int_subject_dn
    TEXT,
    int_not_before
    TIMESTAMPTZ,
    int_not_after
    TIMESTAMPTZ,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );


-- proj_service_ca_provisioners: one row per (service, provisioner name).
CREATE TABLE if NOT EXISTS proj_service_ca_provisioners (
    service
    TEXT
    NOT
    NULL,
    provisioner
    TEXT
    NOT
    NULL,
    provisioner_type
    TEXT,
    min_duration
    TEXT,
    max_duration
    TEXT,
    default_duration
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (service,
                 provisioner
                )
    );


-- proj_service_ca_dns_names: DNS names (SANs) the CA is reachable as.
CREATE TABLE if NOT EXISTS proj_service_ca_dns_names (
    service
    TEXT
    NOT
    NULL,
    dnsname
    TEXT
    NOT
    NULL,
    VALUE
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (service,
                 dnsname
                )
    );


-- proj_dns_stats: aggregate query/block counters per DNS service.
CREATE TABLE if NOT EXISTS proj_dns_stats (
    service
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    total_queries
    BIGINT,
    total_blocked
    BIGINT,
    blocked_pct
    DOUBLE
    PRECISION,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );


-- proj_dns_zones: authoritative zones managed by a DNS service.
CREATE TABLE if NOT EXISTS proj_dns_zones (
    service
    TEXT
    NOT
    NULL,
    ZONE
    TEXT
    NOT
    NULL,
    zone_type
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (service,
                 ZONE
                )
    );


-- proj_dns_records: resource records within a zone.
-- Index on ip enables reverse-lookup correlation with DHCP leases and ARP tables.
CREATE TABLE if NOT EXISTS proj_dns_records (
    service
    TEXT
    NOT
    NULL,
    ZONE
    TEXT
    NOT
    NULL,
    record
    TEXT
    NOT
    NULL,
    ip
    TEXT,
    ttl
    INTEGER,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (service,
                 ZONE,
                 record
                )
    );

CREATE INDEX if NOT EXISTS proj_dns_records_ip_idx
    ON proj_dns_records (ip)
    WHERE ip IS NOT NULL;


-- proj_dhcp_scopes: DHCP address pools.
CREATE TABLE if NOT EXISTS proj_dhcp_scopes (
    service
    TEXT
    NOT
    NULL,
    SCOPE
    TEXT
    NOT
    NULL,
    enabled
    BOOLEAN,
    start_address
    TEXT,
    end_address
    TEXT,
    subnet_mask
    TEXT,
    gateway
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (service,
                 SCOPE
                )
    );


-- proj_dhcp_leases: active and reserved DHCP leases.
-- Indexes:
--   lease (MAC)  → device correlation via ARP table
--   ip           → IP→MAC lookup
--   expires_at   → expiry monitoring (partial — excludes static/reserved entries)
CREATE TABLE if NOT EXISTS proj_dhcp_leases (
    service
    TEXT
    NOT
    NULL,
    SCOPE
    TEXT
    NOT
    NULL,
    lease
    TEXT
    NOT
    NULL, -- MAC address
    ip
    TEXT,
    hostname
    TEXT,
    lease_type
    TEXT,
    expires_at
    TIMESTAMPTZ,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (service,
                 SCOPE,
                 lease
                )
    );

CREATE INDEX if NOT EXISTS proj_dhcp_leases_mac_idx
    ON proj_dhcp_leases (lease);

CREATE INDEX if NOT EXISTS proj_dhcp_leases_ip_idx
    ON proj_dhcp_leases (ip)
    WHERE ip IS NOT NULL;

CREATE INDEX if NOT EXISTS proj_dhcp_leases_expires_idx
    ON proj_dhcp_leases (expires_at)
    WHERE expires_at IS NOT NULL;


-- ── Example queries ───────────────────────────────────────────────────────────

-- All devices with their OS and hardware summary:
--   SELECT s.hostname, s.os_distro, s.os_version, s.kernel_arch,
--          h.cpu_model, h.cpu_cores, h.total_mem_bytes, h.virtualization
--   FROM   proj_systems s
--   JOIN   proj_hardware h USING (device)
--   ORDER  BY s.hostname;

-- Security posture: devices missing secure boot or without a TPM:
--   SELECT s.hostname, sec.secure_boot, sec.tpm_present, sec.selinux_mode
--   FROM   proj_security sec
--   JOIN   proj_systems s USING (device)
--   WHERE  sec.secure_boot IS DISTINCT FROM true
--      OR  sec.tpm_present IS DISTINCT FROM true;

-- Disks with health problems or nearing end of life:
--   SELECT s.hostname, d.name, d.model, d.type,
--          d.smart_health, d.smart_temp_c, d.smart_wear_pct
--   FROM   proj_disks d
--   JOIN   proj_systems s USING (device)
--   WHERE  d.smart_health IS DISTINCT FROM 'PASSED'
--      OR  d.smart_wear_pct > 80;

-- Filesystems above 90% full:
--   SELECT s.hostname, f.filesystem, f.fs_type,
--          pg_size_pretty(f.used_bytes) AS used, f.used_pct
--   FROM   proj_filesystems f
--   JOIN   proj_systems s USING (device)
--   WHERE  f.used_pct > 90
--   ORDER  BY f.used_pct DESC;

-- Containers that are unhealthy or stopped:
--   SELECT s.hostname, c.name, c.image, c.state, c.health, c.restart_count
--   FROM   proj_containers c
--   JOIN   proj_systems s USING (device)
--   WHERE  c.state != 'running' OR c.health NOT IN ('healthy', '')
--   ORDER  BY c.restart_count DESC;

-- Devices with pending security updates or requiring a reboot:
--   SELECT s.hostname, u.manager, u.pending, u.security, u.reboot_required
--   FROM   proj_updates u
--   JOIN   proj_systems s USING (device)
--   WHERE  u.security > 0 OR u.reboot_required = true
--   ORDER  BY u.security DESC NULLS LAST;

-- All admin users across the network:
--   SELECT s.hostname, u.username, u.uid, u.shell, u.disabled
--   FROM   proj_local_users u
--   JOIN   proj_systems s USING (device)
--   WHERE  u.is_admin = true
--   ORDER  BY s.hostname, u.username;

-- Active login sessions right now:
--   SELECT s.hostname, se.username, se.tty, se.login_at, se.host
--   FROM   proj_sessions se
--   JOIN   proj_systems s USING (device)
--   ORDER  BY s.hostname, se.username;

-- What is listening on port 443 across the network?
--   SELECT s.hostname, p.address, p.protocol, p.process_name, p.pid
--   FROM   proj_ports p
--   JOIN   proj_systems s USING (device)
--   WHERE  p.port = 443;

-- Cert expiry — certs expiring in the next 30 days:
--   SELECT s.hostname, c.subject_dn, c.issuer_dn, c.not_after, c.path
--   FROM   proj_device_certs c
--   JOIN   proj_systems s USING (device)
--   WHERE  c.not_after BETWEEN now() AND now() + interval '30 days'
--   ORDER  BY c.not_after;

-- Which devices have a cert issued by CA X?
--   SELECT s.hostname, c.subject_dn, c.not_after, c.path
--   FROM   proj_device_certs c
--   JOIN   proj_systems s USING (device)
--   WHERE  c.issuer_dn = 'CN=My Root CA'
--   ORDER  BY c.not_after;

-- DHCP lease + ARP correlation: find IP→MAC→hostname:
--   SELECT l.ip, l.lease AS mac, l.hostname, l.lease_type, l.expires_at,
--          a.device AS device_with_arp, a.state AS arp_state
--   FROM   proj_dhcp_leases l
--   LEFT JOIN proj_device_arp a ON a.arp = l.ip AND a.mac = l.lease
--   WHERE  l.scope = '192.168.1.0/24'
--   ORDER  BY l.ip;

-- DNS record → DHCP lease cross-reference:
--   SELECT r.record, r.ip, l.hostname AS dhcp_hostname, l.lease AS mac
--   FROM   proj_dns_records r
--   LEFT JOIN proj_dhcp_leases l ON l.ip = r.ip
--   WHERE  r.zone = 'home.lan'
--   ORDER  BY r.record;

-- History of one specific fact (PK lookup → index-only scan):
--   SELECT value_long, collected_at
--   FROM   facts_history
--   WHERE  id = 'Device[router-1].Interface[eth0].SpeedBps'
--   ORDER  BY collected_at DESC;

-- What changed across the whole network in the last 5 minutes:
--   SELECT id, attribute_path, key_values, value_str, value_long, collected_at
--   FROM   facts_history
--   WHERE  collected_at > now() - interval '5 minutes'
--   ORDER  BY collected_at DESC;


-- ═════════════════════════════════════════════════════════════════════════════
-- ITERATION 2 SCHEMA ADDITIONS
-- All guards use IF NOT EXISTS / ADD COLUMN IF NOT EXISTS — safe to re-apply.
-- Apply in dependency order: credentials → agents → users → user_sessions →
--   audit_log → collection_targets → device_aliases →
--   excluded_fingerprints → ALTER devices → ALTER device_fingerprints →
--   report indexes → agent status index
-- ═════════════════════════════════════════════════════════════════════════════


-- ── credentials (no FKs) ─────────────────────────────────────────────────────

CREATE TABLE if NOT EXISTS credentials (
    credential_id
    UUID
    NOT
    NULL
    DEFAULT
    gen_random_uuid(
                   ) PRIMARY KEY,
    NAME TEXT NOT NULL,
    TYPE TEXT NOT NULL, -- ssh-key|ssh-password|snmp|api-token
    encrypted_blob BYTEA NOT NULL, -- .NET Data Protection ciphertext
    created_at TIMESTAMPTZ NOT NULL DEFAULT now (
                                                ),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now (
                                                )
    );
CREATE INDEX if NOT EXISTS credentials_keyset_idx ON credentials (created_at DESC, credential_id);


-- ── agents (no FKs) ──────────────────────────────────────────────────────────

CREATE TABLE if NOT EXISTS agents (
    agent_id
    UUID
    NOT
    NULL
    PRIMARY
    KEY,
    hostname
    TEXT
    NOT
    NULL,
    status
    TEXT
    NOT
    NULL
    DEFAULT
    'pending', -- pending|approved|disabled
    api_key_hash
    TEXT
    NOT
    NULL,
    last_heartbeat
    TIMESTAMPTZ,
    ZONE
    TEXT,
    version
    TEXT,
    passive_discovery_mode
    TEXT,      -- full|degraded
    os
    TEXT,      -- linux|macos|windows
    arch
    TEXT,      -- x64|arm64|x86|...
    ip_address
    TEXT,      -- primary IP at registration time
    created_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );
CREATE UNIQUE INDEX if NOT EXISTS agents_api_key_hash_idx ON agents (api_key_hash);
CREATE INDEX if NOT EXISTS agents_keyset_idx ON agents (created_at DESC, agent_id);


-- ── users + user_sessions ─────────────────────────────────────────────────────

CREATE TABLE if NOT EXISTS users (
    user_id
    UUID
    NOT
    NULL
    DEFAULT
    gen_random_uuid(
                   ) PRIMARY KEY,
    username TEXT NOT NULL UNIQUE,
    password_hash TEXT NOT NULL, -- PBKDF2 via PasswordHasher
    ROLE TEXT NOT NULL DEFAULT 'viewer', -- admin|viewer
    created_at TIMESTAMPTZ NOT NULL DEFAULT now (
                                                )
    );

CREATE TABLE if NOT EXISTS user_sessions (
    session_id
    TEXT
    NOT
    NULL
    PRIMARY
    KEY, -- high-entropy random
    user_id
    UUID
    NOT
    NULL
    REFERENCES
    users(
    user_id
         ) ON DELETE CASCADE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now (
                                                ),
    last_seen TIMESTAMPTZ NOT NULL DEFAULT now (
                                               ),
    expires_at TIMESTAMPTZ NOT NULL,
    user_agent TEXT,
    ip_address INET
    );
CREATE INDEX if NOT EXISTS user_sessions_user_idx ON user_sessions (user_id);
CREATE INDEX if NOT EXISTS user_sessions_expires_idx ON user_sessions (expires_at);


-- ── audit_log ─────────────────────────────────────────────────────────────────

CREATE TABLE if NOT EXISTS audit_log (
    id
    BIGINT
    GENERATED
    ALWAYS AS
    IDENTITY
    PRIMARY
    KEY,
    occurred_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    actor TEXT NOT NULL, -- "user:<username>" | "agent:<id>" | "system"
    ACTION TEXT NOT NULL, -- login|logout|agent.approve|target.create|device.merge|...
    target_ref TEXT, -- affected entity id
    detail JSONB
    );
CREATE INDEX if NOT EXISTS audit_log_keyset_idx ON audit_log (occurred_at DESC, id);


-- ── collection_targets (FKs to agents + credentials) ─────────────────────────

CREATE TABLE if NOT EXISTS collection_targets (
    target_id
    UUID
    NOT
    NULL
    DEFAULT
    gen_random_uuid(
                   ) PRIMARY KEY,
    agent_id UUID NOT NULL REFERENCES agents (agent_id
                                             ) ON DELETE CASCADE,
    address TEXT NOT NULL,
    protocol TEXT NOT NULL, -- ssh|snmp|http|cert|...
    credential_id UUID REFERENCES credentials (credential_id
                                              ),
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    intervals_override JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now (
                                                ),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now (
                                                )
    );
CREATE INDEX if NOT EXISTS collection_targets_agent_idx ON collection_targets (agent_id);
CREATE INDEX if NOT EXISTS collection_targets_keyset_idx ON collection_targets (created_at DESC, target_id);


-- ── device_aliases (FK to devices) ───────────────────────────────────────────

CREATE TABLE if NOT EXISTS device_aliases (
    alias_device_id
    UUID
    NOT
    NULL
    PRIMARY
    KEY,
    survivor_device_id
    UUID
    NOT
    NULL
    REFERENCES
    devices(
    device_id
           ),
    merged_at TIMESTAMPTZ NOT NULL DEFAULT now (
                                               )
    );
CREATE INDEX if NOT EXISTS device_aliases_survivor_idx ON device_aliases (survivor_device_id);


-- ── excluded_fingerprints ─────────────────────────────────────────────────────

CREATE TABLE if NOT EXISTS excluded_fingerprints (
    fp_type
    TEXT
    NOT
    NULL,
    fp_value
    TEXT
    NOT
    NULL,
    excluded_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    excluded_by TEXT,
    PRIMARY KEY (fp_type,
                 fp_value
                )
    );


-- ── ALTER devices — add columns ───────────────────────────────────────────────

ALTER TABLE devices
    ADD COLUMN if NOT EXISTS management_status TEXT NOT NULL DEFAULT 'managed',
    ADD COLUMN IF NOT EXISTS merged_from UUID[] NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT now();

-- Backfill: existing rows already have updated_at = now() from the default;
-- set it to created_at so it reflects the actual creation time.
UPDATE devices
SET
    updated_at = created_at
WHERE
    updated_at > created_at;

CREATE INDEX if NOT EXISTS devices_status_idx ON devices (management_status);


-- ── ALTER device_fingerprints — add columns ───────────────────────────────────

ALTER TABLE device_fingerprints
    ADD COLUMN if NOT EXISTS last_seen TIMESTAMPTZ NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS SOURCE TEXT;

UPDATE device_fingerprints
SET
    last_seen = first_seen
WHERE
    last_seen > first_seen;


-- ── Report keyset-pagination indexes ─────────────────────────────────────────

-- hosts report sorts by (hostname, device)
CREATE INDEX if NOT EXISTS proj_systems_hostname_idx
    ON proj_systems (hostname, device);

-- certificate inventory sorts by expiry
CREATE INDEX if NOT EXISTS proj_device_certs_expiry_idx
    ON proj_device_certs (not_after, device);

-- change feed: stable keyset cursor requires (collected_at DESC, id) tuple
CREATE INDEX if NOT EXISTS facts_history_changefeed_idx
    ON facts_history (collected_at DESC, id);


-- ── Agent status index ────────────────────────────────────────────────────────

CREATE INDEX if NOT EXISTS agents_status_idx ON agents (status);
