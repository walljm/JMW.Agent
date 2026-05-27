-- 0018 entity model extensions: posture, containers, interface profiles

-- Host update posture (one row per system, latest state).
CREATE TABLE IF NOT EXISTS system_update_status (
    system_id TEXT PRIMARY KEY REFERENCES systems(id) ON DELETE CASCADE,
    manager TEXT,                                  -- apt|dnf|yum|brew|softwareupdate|windowsupdate
    pending INTEGER NOT NULL DEFAULT 0,
    security INTEGER NOT NULL DEFAULT 0,
    reboot_required INTEGER NOT NULL DEFAULT 0,
    checked_at TEXT,
    updated_at TEXT NOT NULL
);

-- Pending updates detail (capped list per system).
CREATE TABLE IF NOT EXISTS system_pending_updates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    system_id TEXT NOT NULL REFERENCES systems(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    current_version TEXT,
    new_version TEXT,
    source TEXT,
    security INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_pending_updates_system ON system_pending_updates(system_id);

-- Security posture (one row per system).
CREATE TABLE IF NOT EXISTS system_security_posture (
    system_id TEXT PRIMARY KEY REFERENCES systems(id) ON DELETE CASCADE,
    firewall_provider TEXT,
    firewall_enabled INTEGER,
    firewall_default_policy TEXT,
    tpm_present INTEGER,
    tpm_version TEXT,
    secure_boot INTEGER,
    selinux_mode TEXT,
    apparmor_mode TEXT,
    updated_at TEXT NOT NULL
);

-- Antivirus/EDR products (multiple per system).
CREATE TABLE IF NOT EXISTS system_av_products (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    system_id TEXT NOT NULL REFERENCES systems(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    enabled INTEGER NOT NULL DEFAULT 1,
    realtime_protected INTEGER NOT NULL DEFAULT 0,
    up_to_date INTEGER NOT NULL DEFAULT 1,
    signature_version TEXT,
    signature_age TEXT,
    last_scan TEXT,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_av_products_system ON system_av_products(system_id);

-- Disk encryption volumes.
CREATE TABLE IF NOT EXISTS system_encrypted_volumes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    system_id TEXT NOT NULL REFERENCES systems(id) ON DELETE CASCADE,
    mountpoint TEXT,
    device TEXT,
    enc_type TEXT,                                 -- bitlocker|luks|filevault|apfs-encrypted
    enc_status TEXT,                               -- on|off|partial|suspended
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_encrypted_volumes_system ON system_encrypted_volumes(system_id);

-- OS services (failed/abnormal only by default).
CREATE TABLE IF NOT EXISTS system_services (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    system_id TEXT NOT NULL REFERENCES systems(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    display_name TEXT,
    state TEXT,                                    -- running|stopped|failed
    start_mode TEXT,                               -- auto|manual|disabled
    sub_state TEXT,
    exit_code INTEGER,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_system_services_system ON system_services(system_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_system_services_unique ON system_services(system_id, name);

-- Local user accounts.
CREATE TABLE IF NOT EXISTS system_local_users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    system_id TEXT NOT NULL REFERENCES systems(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    uid TEXT,
    gid TEXT,
    home_dir TEXT,
    shell TEXT,
    is_admin INTEGER NOT NULL DEFAULT 0,
    disabled INTEGER NOT NULL DEFAULT 0,
    last_login TEXT,
    password_age_days INTEGER,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_local_users_system ON system_local_users(system_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_local_users_unique ON system_local_users(system_id, name);

-- GPU list per hardware.
CREATE TABLE IF NOT EXISTS hardware_gpus (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    hardware_id TEXT NOT NULL REFERENCES hardware(id) ON DELETE CASCADE,
    vendor TEXT,
    model TEXT,
    driver_version TEXT,
    vram_bytes INTEGER,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_gpus_hardware ON hardware_gpus(hardware_id);

-- Container runtime detail (per system, latest state).
CREATE TABLE IF NOT EXISTS system_container_engine (
    system_id TEXT PRIMARY KEY REFERENCES systems(id) ON DELETE CASCADE,
    reachable INTEGER NOT NULL DEFAULT 0,
    engine_id TEXT,
    version TEXT,
    api_version TEXT,
    os TEXT,
    os_type TEXT,
    architecture TEXT,
    storage_driver TEXT,
    cgroup_driver TEXT,
    cgroup_version TEXT,
    logging_driver TEXT,
    root_dir TEXT,
    swarm_node_id TEXT,
    swarm_state TEXT,
    updated_at TEXT NOT NULL
);

-- Containers running on a system.
CREATE TABLE IF NOT EXISTS system_containers (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    system_id TEXT NOT NULL REFERENCES systems(id) ON DELETE CASCADE,
    container_id TEXT NOT NULL,
    name TEXT NOT NULL,
    image TEXT,
    image_id TEXT,
    state TEXT,                                    -- running|exited|paused|created|dead
    status TEXT,                                   -- human: "Up 3 hours"
    created_at_container TEXT,
    started_at TEXT,
    health_status TEXT,
    restart_count INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_containers_system ON system_containers(system_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_containers_unique ON system_containers(system_id, container_id);

-- Interface mDNS advertised services.
CREATE TABLE IF NOT EXISTS interface_mdns_services (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    interface_id TEXT NOT NULL REFERENCES interfaces(id) ON DELETE CASCADE,
    service_type TEXT NOT NULL,                    -- e.g. _http._tcp
    instance_name TEXT,
    port INTEGER,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_mdns_services_interface ON interface_mdns_services(interface_id);

-- Interface TLS SANs observed during scans.
CREATE TABLE IF NOT EXISTS interface_tls_sans (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    interface_id TEXT NOT NULL REFERENCES interfaces(id) ON DELETE CASCADE,
    port INTEGER NOT NULL,
    san TEXT NOT NULL,
    issuer TEXT,
    not_after TEXT,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_tls_sans_interface ON interface_tls_sans(interface_id);
