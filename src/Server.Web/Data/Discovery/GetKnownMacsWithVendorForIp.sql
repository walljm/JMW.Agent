-- Twin of GetKnownMacsForIp.sql (keep both in sync) for the Home Assistant IP-join
-- (docs/plans/ha-device-enrichment.md §5): same agent-scoped IP -> MAC lookup, plus each
-- candidate's IEEE-registered NIC vendor (oui_vendor(mac), migration 0019) so the caller can
-- cross-check against the HA device's own self-reported manufacturer before accepting a
-- MAC-less device's recovered identity — a second, independent signal beyond "this agent's
-- own LAN reported this IP once."
SELECT DISTINCT
    norm.mac
  , oui_vendor(norm.mac) AS vendor
FROM
    (
        SELECT
            a.mac AS mac
        FROM
            proj_device_arp a
        WHERE
              a.arp = $1
          AND a.mac IS NOT NULL
          AND (a.agent_id IS NULL OR a.agent_id = $2)
        UNION ALL
        SELECT
            l.lease
        FROM
            proj_dhcp_leases l
        WHERE
              l.ip = $1
          AND l.lease IS NOT NULL
          AND (l.agent_id IS NULL OR l.agent_id = $2)
        UNION ALL
        SELECT
            l.lease
        FROM
            proj_dhcp_local_leases l
        WHERE
              l.ip = $1
          AND l.lease IS NOT NULL
          AND (l.agent_id IS NULL OR l.agent_id = $2)
        UNION ALL
        SELECT
            d.mac
        FROM
            proj_discovered d
        WHERE
              d.discovered = $1
          AND d.mac IS NOT NULL
          AND (d.agent_id IS NULL OR d.agent_id = $2)
        ) norm
WHERE
    length(norm.mac) = 12
