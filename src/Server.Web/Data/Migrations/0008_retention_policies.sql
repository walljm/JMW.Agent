-- ── Retention policies ────────────────────────────────────────────────────────
--
-- One row per table managed by RetentionService. The service deletes rows where
--   {time_column} < now() - stale_after
-- for all enabled policies with a non-NULL stale_after.
--
-- Tables absent from this table are never touched by retention.
-- Set enabled = false to suspend pruning for a table without losing its interval.

CREATE TABLE if NOT EXISTS retention_policies (
    TABLE_NAME
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    category
    TEXT
    NOT
    NULL,     -- volatile|network|dynamic|stable|history|system
    time_column
    TEXT
    NOT
    NULL
    DEFAULT
    'updated_at',
    stale_after
    INTERVAL, -- NULL = never auto-prune
    enabled
    BOOLEAN
    NOT
    NULL
    DEFAULT
    TRUE,
    notes
    TEXT
                                              );


-- ── Volatile (1 day) ─────────────────────────────────────────────────────────

INSERT INTO retention_policies
(
      table_name
    , category
    , stale_after
    , notes
)
VALUES
(
    'proj_processes'
    , 'volatile'
    , INTERVAL '1 day'
    , 'PID snapshots; PIDs are recycled across reboots so stale rows are meaningless within hours'
)
     ,
(
    'proj_sessions'
    , 'volatile'
    , INTERVAL '1 day'
    , 'Active login sessions; stale rows linger when a session closes without a collector update'
)
    ON CONFLICT (TABLE_NAME) DO NOTHING;


-- ── Network State (7 days) ───────────────────────────────────────────────────

INSERT INTO retention_policies
(
      table_name
    , category
    , stale_after
    , notes
)
VALUES
(
    'proj_device_arp'
    , 'network'
    , INTERVAL '7 days'
    , 'ARP cache; entries for silent neighbors accumulate quickly'
)
     ,
(
    'proj_device_routes'
    , 'network'
    , INTERVAL '7 days'
    , 'Routing table snapshot; stale entries mislead topology queries'
)
     ,
(
    'proj_discovered'
    , 'network'
    , INTERVAL '7 days'
    , 'Passive discovery results; neighbors may leave the network without notice'
)
     ,
(
    'proj_dhcp_leases'
    , 'network'
    , INTERVAL '7 days'
    , 'DHCP leases; also prunable by expires_at via a separate RetentionService pass'
)
     ,
(
    'proj_dhcp_local_leases'
    , 'network'
    , INTERVAL '7 days'
    , 'DHCP leases from local lease files; expires_at is ISO-8601 text, prune by updated_at'
)
    ON CONFLICT (TABLE_NAME) DO NOTHING;


-- ── Dynamic (30 days) ────────────────────────────────────────────────────────

INSERT INTO retention_policies
(
      table_name
    , category
    , stale_after
    , notes
)
VALUES
(
    'proj_containers'
    , 'dynamic'
    , INTERVAL '30 days'
    , 'Docker containers; ephemeral by nature'
)
     ,
(
    'proj_ports'
    , 'dynamic'
    , INTERVAL '30 days'
    , 'Listening ports; shift with service changes and restarts'
)
     ,
(
    'proj_device_services'
    , 'dynamic'
    , INTERVAL '30 days'
    , 'Only failed/degraded services are recorded; stale entries suggest a service that recovered'
)
     ,
(
    'proj_reboots_history'
    , 'dynamic'
    , INTERVAL '30 days'
    , 'Boot event log; collector caps at 20 rows per device, but stale devices accumulate'
)
     ,
(
    'proj_dns_stats'
    , 'dynamic'
    , INTERVAL '30 days'
    , 'Aggregate query/block counters; meaningful only when fresh'
)
     ,
(
    'proj_dns_records'
    , 'dynamic'
    , INTERVAL '30 days'
    , 'DNS resource records; change with zone edits'
)
    ON CONFLICT (TABLE_NAME) DO NOTHING;


-- ── Stable (90 days) ─────────────────────────────────────────────────────────
-- Hardware and OS rarely change. 90 days covers extended offline periods
-- and decommissioned devices before the data becomes actively misleading.

INSERT INTO retention_policies
(
      table_name
    , category
    , stale_after
    , notes
)
VALUES
     -- Device singletons
(
    'proj_devices'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_systems'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_hardware'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_security'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_batteries'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_updates'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_docker'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_snmp_device'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_reboots'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
     -- Device lists
(
    'proj_hardware_inventory'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_interfaces'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_disks'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_filesystems'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_local_users'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_gpu'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_packages'
    , 'stable'
    , INTERVAL '90 days'
    , 'Capped at 2000 rows/device by collector; prune stale devices'
)
     ,
(
    'proj_device_certs'
    , 'stable'
    , INTERVAL '90 days'
    , 'Also consider pruning by not_after < now() - interval ''30 days'' for long-expired certs'
)
     ,
(
    'proj_device_trusted_cas'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
     -- Service projections
(
    'proj_services'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_service_ca'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_service_ca_provisioners'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_service_ca_dns_names'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_dns_zones'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_dhcp_scopes'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_home_assistant'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
     ,
(
    'proj_home_assistant_addons'
    , 'stable'
    , INTERVAL '90 days'
    , NULL
)
    ON CONFLICT (TABLE_NAME) DO NOTHING;


-- ── History (90 days, pruned on collected_at not updated_at) ─────────────────
-- facts_history is the primary storage growth driver.
-- RetentionService must use time_column = 'collected_at' here, not 'updated_at'.
-- If the table is ever partitioned by month, switch to DROP PARTITION instead of DELETE.

INSERT INTO retention_policies
(
      table_name
    , category
    , time_column
    , stale_after
    , notes
)
VALUES
    (
          'facts_history'
        , 'history'
        , 'collected_at'
        , INTERVAL '90 days'
        , 'Append-only change log; prune on collected_at. Switch to DROP PARTITION if table is ever partitioned by month.'
    ) ON CONFLICT (TABLE_NAME) DO NOTHING;


-- ── System (special handling) ─────────────────────────────────────────────────

INSERT INTO retention_policies
(
      table_name
    , category
    , time_column
    , stale_after
    , notes
)
VALUES
(
    'user_sessions'
    , 'system'
    , 'expires_at'
    , INTERVAL '0 seconds'
    , 'Prune all rows where expires_at < now(). Run on startup, not just nightly.'
)
     ,
(
    'audit_log'
    , 'system'
    , 'occurred_at'
    , INTERVAL '730 days'
    , 'Compliance retention. Do not reduce below 1 year without legal sign-off.'
)
    ON CONFLICT (TABLE_NAME) DO NOTHING;


-- ── Index for the RetentionService query ─────────────────────────────────────

CREATE INDEX if NOT EXISTS retention_policies_category_idx
    ON retention_policies (category)
    WHERE enabled = TRUE;
