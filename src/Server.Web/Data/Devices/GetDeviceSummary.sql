SELECT
    d.device_id
  , d.management_status
    -- proj_systems.hostname only exists for agent-managed devices (self-reported OS
    -- hostname). A passively-discovered device (no agent) never gets one, even when
    -- other observers already know its name via mDNS/Cast/SSDP — fall back to the best
    -- name any observer has recorded for this device's MAC in proj_discovered.
  , COALESCE(
        s.hostname,
        (
            -- pd_disc.obscured_mac IS NULL: exclude Google Wifi/OnHub rows whose mac was filled
            -- in by obscured-MAC reconstruction rather than direct observation — see the same
            -- guard in HostsApi.cs's `disc` lateral for why matching those here would smear a
            -- stale mDNS advertisement's name onto whatever device the MAC actually resolves to.
            SELECT COALESCE(pd_disc.friendly_name, pd_disc.hostname)
            FROM device_fingerprints fp
                JOIN proj_discovered pd_disc ON pd_disc.mac = fp.fp_value
            WHERE fp.device_id = d.device_id AND fp.fp_type = 'mac'
              AND pd_disc.obscured_mac IS NULL
              AND COALESCE(pd_disc.friendly_name, pd_disc.hostname) IS NOT NULL
            ORDER BY pd_disc.updated_at DESC
            LIMIT 1
        )
    ) AS hostname
  , s.os_family
  , s.os_distro
    -- Inferred OS-distro guess (OsFromSnmpSysDescrDerivation et al.) — surfaced separately so
    -- the caller can label it "inferred" rather than self-reported; only meaningful when
    -- os_distro is NULL. See docs/plans/vendor-derivation-updates.md §5.
  , s.os_distro_guess
  , CASE
        WHEN s.device IS NULL
            THEN NULL
        ELSE s.updated_at
    END AS last_seen
  , pd.vendor
    -- Inferred vendor guess (VendorFromOsDistroDerivation et al.) — surfaced separately from
    -- `vendor` so the caller can label it as inferred rather than self-reported; only meaningful
    -- when `vendor` is NULL. See docs/plans/vendor-derivation-updates.md §3.
  , pd.vendor_guess
    -- Which collector's fact produced the current pd.vendor value — pd.vendor is a
    -- last-write-wins projection of Device[].Vendor, so the latest facts_history row for
    -- that path/device IS the winning row.
  , (
        SELECT h.source_name
        FROM facts_history h
        WHERE h.attribute_path = 'Device[].Vendor'
          AND h.key_values ->> 'Device' = d.device_id::text
        ORDER BY h.collected_at DESC
        LIMIT 1
    ) AS vendor_source_name
  , pd.kind
  , h.cpu_model
  , h.cpu_cores
  , h.total_mem_bytes
  , h.system_vendor
  , h.system_model
  , h.system_serial
    -- Best IP: last-seen (DHCP/Google Wifi) or an ARP neighbor IP for the device's
    -- MAC, always preferring IPv4 over IPv6, then the MOST RECENTLY seen so the address
    -- follows DHCP moves (a fresh ARP sighting beats a stale last_seen_ip).
  , (
        SELECT cand.ip
        FROM (
            SELECT s.last_seen_ip AS ip, s.updated_at AS seen WHERE s.last_seen_ip IS NOT NULL
            UNION ALL
            SELECT a.arp, a.updated_at
            FROM proj_device_arp a
                JOIN device_fingerprints fp
                    ON fp.fp_type = 'mac'
                   AND a.mac = fp.fp_value
            WHERE fp.device_id = d.device_id AND a.arp IS NOT NULL
        ) cand
        WHERE cand.ip IS NOT NULL
          -- Never identify a device by a loopback / link-local / unspecified address.
          AND ip_identity_rank(cand.ip) < 99
        ORDER BY
            (cand.ip LIKE '%:%')       -- IPv4 before IPv6
          , ip_identity_rank(cand.ip)  -- private LAN before public/WAN
          , cand.seen DESC NULLS LAST  -- most recently seen IP wins (follows DHCP moves)
        LIMIT 1
    ) AS last_seen_ip
FROM
    devices                d
    LEFT JOIN proj_systems s
    ON s.device = d.device_id::text
LEFT JOIN proj_devices  pd
ON pd.device = d.device_id::text
    LEFT JOIN proj_hardware h ON h.device = d.device_id::text
WHERE
    d.device_id = $1
