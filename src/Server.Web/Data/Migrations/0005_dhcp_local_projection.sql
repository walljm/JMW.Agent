SET
search_path TO jmwdiscovery, PUBLIC;

-- proj_dhcp_local_leases: DHCP leases read from local lease files
-- (dnsmasq, ISC dhcpd, Kea, OpenWrt). Distinct from proj_dhcp_leases,
-- which is populated by service API collectors.
CREATE TABLE if NOT EXISTS proj_dhcp_local_leases (
    device
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
    expires_at
    TEXT, -- ISO-8601 string (nullable for static leases)
    SOURCE
    TEXT, -- "dnsmasq" | "isc-dhcpd" | "kea" | "openwrt"
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 lease
                )
    );

CREATE INDEX if NOT EXISTS proj_dhcp_local_leases_ip_idx
    ON proj_dhcp_local_leases (ip)
    WHERE ip IS NOT NULL;
