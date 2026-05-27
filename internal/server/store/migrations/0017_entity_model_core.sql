-- 0017 entity model core: hardware, systems, interfaces, observations, services, disks

-- Hardware represents a physical (or virtual) device chassis. The stable identity
-- anchor when a machine is re-imaged or changes OS.
CREATE TABLE IF NOT EXISTS hardware (
    id TEXT PRIMARY KEY,                           -- server-generated UUID
    system_serial TEXT,                            -- DMI system serial (nullable, not unique — some vendors reuse)
    board_serial TEXT,
    system_vendor TEXT,
    system_model TEXT,
    board_vendor TEXT,
    board_model TEXT,
    cpu_model TEXT,
    cpu_cores INTEGER,
    cpu_logical_cores INTEGER,
    total_mem_bytes INTEGER,
    virtualization TEXT,                           -- none|kvm|vmware|docker|wsl|hyperv
    chassis_type TEXT,                             -- laptop|desktop|server|vm|sbc|tablet|other
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_hardware_serial ON hardware(system_serial) WHERE system_serial IS NOT NULL;

-- Systems represent an OS installation on a Hardware. Re-image = new System row.
CREATE TABLE IF NOT EXISTS systems (
    id TEXT PRIMARY KEY,                           -- server-generated UUID
    hardware_id TEXT NOT NULL REFERENCES hardware(id) ON DELETE CASCADE,
    agent_id TEXT REFERENCES agents(id) ON DELETE SET NULL,  -- set when agent correlates
    hostname TEXT NOT NULL,
    os_family TEXT NOT NULL,                       -- linux|darwin|windows|freebsd
    os_distro TEXT,
    os_version TEXT,
    os_build TEXT,
    kernel TEXT,
    kernel_arch TEXT,
    timezone TEXT,
    boot_time TEXT,
    install_date TEXT,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_systems_hardware ON systems(hardware_id);
CREATE INDEX IF NOT EXISTS idx_systems_agent ON systems(agent_id) WHERE agent_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_systems_hostname ON systems(hostname);

-- Interfaces represent a network interface (physical or virtual) attached to a System.
-- MAC is the primary identity key for network-observed interfaces.
CREATE TABLE IF NOT EXISTS interfaces (
    id TEXT PRIMARY KEY,                           -- server-generated UUID
    system_id TEXT REFERENCES systems(id) ON DELETE SET NULL,
    hardware_id TEXT NOT NULL REFERENCES hardware(id) ON DELETE CASCADE,
    mac TEXT NOT NULL,
    name TEXT,                                     -- eth0, en0, etc. (nullable — DHCP-only sees MAC)
    iface_type TEXT,                               -- ethernet|wifi|virtual|loopback|bridge
    mtu INTEGER,
    link_speed_mbps INTEGER,
    is_up INTEGER NOT NULL DEFAULT 1,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_interfaces_mac ON interfaces(mac);
CREATE INDEX IF NOT EXISTS idx_interfaces_system ON interfaces(system_id) WHERE system_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_interfaces_hardware ON interfaces(hardware_id);

-- Interface addresses (one interface can have multiple IPv4/IPv6).
CREATE TABLE IF NOT EXISTS interface_addresses (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    interface_id TEXT NOT NULL REFERENCES interfaces(id) ON DELETE CASCADE,
    address TEXT NOT NULL,                         -- CIDR notation (e.g. 192.168.1.5/24)
    family TEXT NOT NULL,                          -- ipv4|ipv6
    scope TEXT,                                    -- global|link-local|host
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_iface_addr_unique ON interface_addresses(interface_id, address);
CREATE INDEX IF NOT EXISTS idx_iface_addr_address ON interface_addresses(address);

-- Observations are timestamped sighting records from any source.
-- They reference the interface (MAC-based identity) and carry the raw payload
-- as JSON for provenance. The pipeline creates/updates entity state from these.
CREATE TABLE IF NOT EXISTS observations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    interface_id TEXT NOT NULL REFERENCES interfaces(id) ON DELETE CASCADE,
    source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
    observed_at TEXT NOT NULL,
    obs_type TEXT NOT NULL,                        -- dhcp-lease|dns-query|arp-scan|agent-heartbeat|nmap-host
    raw_json TEXT,                                 -- full observation payload for audit/replay
    created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_observations_interface ON observations(interface_id);
CREATE INDEX IF NOT EXISTS idx_observations_source ON observations(source_id);
CREATE INDEX IF NOT EXISTS idx_observations_time ON observations(observed_at);
CREATE INDEX IF NOT EXISTS idx_observations_type_time ON observations(obs_type, observed_at);

-- Hostname aliases: an interface can be known by multiple names over time.
-- The resolver picks the canonical one by priority.
CREATE TABLE IF NOT EXISTS hostname_aliases (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    interface_id TEXT NOT NULL REFERENCES interfaces(id) ON DELETE CASCADE,
    hostname TEXT NOT NULL,
    source_kind TEXT NOT NULL,                     -- agent|dns-ptr|dhcp|mdns|netbios|user
    priority INTEGER NOT NULL DEFAULT 50,          -- lower = higher priority
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_hostname_aliases_interface ON hostname_aliases(interface_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_hostname_aliases_unique ON hostname_aliases(interface_id, hostname, source_kind);

-- Services detected on a network interface (open ports / mDNS / DHCP fingerprint).
CREATE TABLE IF NOT EXISTS services (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    interface_id TEXT NOT NULL REFERENCES interfaces(id) ON DELETE CASCADE,
    proto TEXT NOT NULL,                           -- tcp|udp
    port INTEGER NOT NULL,
    service_name TEXT,                             -- http|ssh|dns|... (nmap/mdns identified)
    product TEXT,                                  -- e.g. "Apache httpd"
    version TEXT,
    banner TEXT,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_services_interface ON services(interface_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_services_unique ON services(interface_id, proto, port);

-- Disks are physical storage devices attached to a System.
CREATE TABLE IF NOT EXISTS disks (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    system_id TEXT NOT NULL REFERENCES systems(id) ON DELETE CASCADE,
    name TEXT NOT NULL,                            -- sda, nvme0n1, disk0
    model TEXT,
    serial TEXT,
    size_bytes INTEGER,
    disk_type TEXT,                                -- hdd|ssd|nvme|virtual|unknown
    removable INTEGER NOT NULL DEFAULT 0,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_disks_system ON disks(system_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_disks_unique ON disks(system_id, name);

-- SMART attributes for a disk (latest snapshot, not historical).
CREATE TABLE IF NOT EXISTS disk_smart_attributes (
    disk_id INTEGER NOT NULL REFERENCES disks(id) ON DELETE CASCADE,
    overall_health TEXT,
    temperature_c REAL,
    power_on_hours INTEGER,
    power_cycle_count INTEGER,
    reallocated_sectors INTEGER,
    pending_sectors INTEGER,
    uncorrectable_errors INTEGER,
    media_wearout_pct REAL,
    percentage_used REAL,
    available_spare_pct REAL,
    data_units_read_gb REAL,
    data_units_written_gb REAL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (disk_id)
);

-- Disk partitions.
CREATE TABLE IF NOT EXISTS disk_partitions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    disk_id INTEGER NOT NULL REFERENCES disks(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    mountpoint TEXT,
    fs_type TEXT,
    size_bytes INTEGER,
    label TEXT,
    uuid TEXT
);
CREATE INDEX IF NOT EXISTS idx_disk_partitions_disk ON disk_partitions(disk_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_disk_partitions_unique ON disk_partitions(disk_id, name);
