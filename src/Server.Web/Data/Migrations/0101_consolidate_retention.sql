-- Consolidate retention into a few clear tiers so the forced-recollect cadence (which repairs the
-- agent-cache / server-prune divergence) is governed by one number, not a dozen scattered ones.
-- Prior tiers (volatile/network/dynamic/stable) collapse into:
--
--   ephemeral (2d)  — pure network-presence: ARP + local DHCP leases. The agent ALWAYS re-sends
--                     these (they bypass delta-tracking; see Agent.IsEphemeralPresenceSource), so a
--                     2d window never gaps and a departed device drops out fast. This is a live
--                     snapshot of "who's on the network now".
--   steady (30d)    — every delta-suppressible current-state projection. A present device's rows are
--                     refilled well inside 30d by the ~7d staggered forced re-collect, so they never
--                     prune; a departed device ages out at 30d. The re-collect cadence MUST stay
--                     comfortably below this window (~1/4) — matching them would race the prune.
--   history (90d)   — facts_history / change_events: time-series, meant to age out, never re-sent.
--
-- Acute fix folded in: materialization_facts moves 7d -> 30d (steady). At 7d it silently emptied a
-- stable device's identity facts (name/type/serial) because the delta-tracked source never resent.
--
-- Left as-is (operational, not agent-sourced current-state, orthogonal to the divergence):
--   agent_cycles (volatile 7d), audit_log (system 730d), user_sessions (system, by expiry).
--
-- category is a display/grouping label only (RetentionApi / Settings page) with no CHECK constraint
-- and no code that matches on its value, so renaming tiers is safe. Idempotent: re-running sets the
-- same values.

-- ── ephemeral (2d) ──────────────────────────────────────────────────────────
UPDATE retention_policies
SET category = 'ephemeral', stale_after = INTERVAL '2 days'
WHERE table_name IN ('proj_device_arp', 'proj_dhcp_local_leases');

-- ── steady (30d) ────────────────────────────────────────────────────────────
UPDATE retention_policies
SET category = 'steady', stale_after = INTERVAL '30 days'
WHERE table_name IN (
    -- former network tier (minus the two ephemeral above)
    'materialization_facts', 'proj_device_routes', 'proj_dhcp_leases', 'proj_discovered',
    'proj_discovered_services', 'proj_discovered_tls', 'proj_docker_networks',
    -- former dynamic tier
    'proj_containers', 'proj_dns_records', 'proj_dns_stats', 'proj_ports',
    -- former stable tier
    'proj_device_certs', 'proj_devices', 'proj_dhcp_scopes', 'proj_disks', 'proj_dns_zones',
    'proj_filesystems', 'proj_hardware', 'proj_hardware_inventory', 'proj_interfaces',
    'proj_service_ca', 'proj_service_ca_provisioners', 'proj_services', 'proj_systems'
);
