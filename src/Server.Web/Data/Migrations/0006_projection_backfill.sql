SET
search_path TO jmwdiscovery, PUBLIC;

-- Backfill projection tables that exist in the canonical schema/runtime
-- but were missing from the ordered migration set.

CREATE TABLE if NOT EXISTS proj_gpu (
    device
    TEXT
    NOT
    NULL,
    gpu
    TEXT
    NOT
    NULL,
    NAME
    TEXT,
    vendor
    TEXT,
    vram_mb
    BIGINT,
    driver_version
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 gpu
                )
    );

CREATE TABLE if NOT EXISTS proj_packages (
    device
    TEXT
    NOT
    NULL,
    package
    TEXT
    NOT
    NULL,
    version
    TEXT,
    manager
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 package
                )
    );

CREATE INDEX if NOT EXISTS proj_packages_manager_idx ON proj_packages (manager);

CREATE TABLE if NOT EXISTS proj_reboots (
    device
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    last_boot
    TIMESTAMPTZ,
    count_30d
    BIGINT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );

CREATE INDEX if NOT EXISTS proj_reboots_last_boot_idx ON proj_reboots (last_boot);

CREATE TABLE if NOT EXISTS proj_reboots_history (
    device
    TEXT
    NOT
    NULL,
    boot
    TEXT
    NOT
    NULL,
    boot_time
    TIMESTAMPTZ,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 boot
                )
    );

CREATE TABLE if NOT EXISTS proj_snmp_device (
    device
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    sys_descr
    TEXT,
    sys_name
    TEXT,
    sys_location
    TEXT,
    sys_contact
    TEXT,
    sys_object_id
    TEXT,
    engine_id
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );

CREATE TABLE if NOT EXISTS proj_discovered (
    device
    TEXT
    NOT
    NULL,
    discovered
    TEXT
    NOT
    NULL,
    mac
    TEXT,
    hostname
    TEXT,
    sources
    TEXT,
    onvif_serial
    TEXT,
    roku_serial
    TEXT,
    ssdp_uuid
    TEXT,
    wsd_uuid
    TEXT,
    vendor
    TEXT,
    model
    TEXT,
    firmware
    TEXT,
    tls_cn
    TEXT,
    http_title
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 discovered
                )
    );

CREATE INDEX if NOT EXISTS proj_discovered_mac_idx
    ON proj_discovered (mac)
    WHERE mac IS NOT NULL;

CREATE INDEX if NOT EXISTS proj_discovered_hostname_idx
    ON proj_discovered (hostname)
    WHERE hostname IS NOT NULL;

CREATE TABLE if NOT EXISTS proj_home_assistant (
    service
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    supervisor_version
    TEXT,
    core_version
    TEXT,
    os_version
    TEXT,
    os_board
    TEXT,
    channel
    TEXT,
    hostname
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );

CREATE TABLE if NOT EXISTS proj_home_assistant_addons (
    service
    TEXT
    NOT
    NULL,
    addon
    TEXT
    NOT
    NULL,
    NAME
    TEXT,
    version
    TEXT,
    STATE
    TEXT,
    update_available
    BOOLEAN,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (service,
                 addon
                )
    );
