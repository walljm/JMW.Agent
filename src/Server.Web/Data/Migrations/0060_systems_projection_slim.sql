-- Slim proj_systems to columns with a genuine cross-device query need (hostname, os_family,
-- os_distro feed HostsApi/GetCompositionByOsFamily/ListDevices; last_seen_ip is set directly by
-- the materializer, not FactPath-routed, and is unaffected by this migration).
--
-- os_version/os_build/kernel/kernel_arch/timezone/boot_time/uptime_seconds: read only by the
-- single-device System tab (GetDeviceSystem.sql) — uptime_seconds wasn't even rendered there.
-- Moved to the "OS Details" fact view.
--
-- cpu_percent/mem_used_bytes/mem_total_bytes/mem_used_pct/load_1/load_5/load_15: live
-- performance metrics, likely rewritten on every single collection cycle across the whole
-- fleet, read by NOTHING — not even the device detail page. Moved to the "Resource Usage"
-- fact view.
ALTER TABLE proj_systems
    DROP COLUMN os_version,
    DROP COLUMN os_build,
    DROP COLUMN kernel,
    DROP COLUMN kernel_arch,
    DROP COLUMN timezone,
    DROP COLUMN boot_time,
    DROP COLUMN uptime_seconds,
    DROP COLUMN cpu_percent,
    DROP COLUMN mem_used_bytes,
    DROP COLUMN mem_total_bytes,
    DROP COLUMN mem_used_pct,
    DROP COLUMN load_1,
    DROP COLUMN load_5,
    DROP COLUMN load_15;
