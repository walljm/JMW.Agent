-- Twin of GetKnownMacsForIp.sql (keep both in sync) for the Home Assistant IP-join
-- (docs/plans/ha-device-enrichment.md §5): same agent-scoped IP -> MAC lookup, plus each
-- candidate's IEEE-registered NIC vendor (oui_vendor(mac), migration 0019) so the caller can
-- cross-check against the HA device's own self-reported manufacturer before accepting a
-- MAC-less device's recovered identity — a second, independent signal beyond "this agent's
-- own LAN reported this IP once."
--
-- Scope is agent $2 PLUS every agent proven co-located with it (agent_colocation — same L2
-- broadcast domain, proven via >= 3 shared globally-unique ARP MACs), so a MAC captured by one
-- same-LAN agent is recallable from a lookup keyed on another. See GetKnownMacsForIp.sql for the
-- full rationale; keep the scope logic identical between the two.
WITH scope AS (
    SELECT $2::uuid AS aid
    UNION
    SELECT other_agent FROM agent_colocation WHERE agent = $2
)
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
          AND (a.agent_id IS NULL OR a.agent_id IN (SELECT aid FROM scope))
        UNION ALL
        SELECT
            l.lease
        FROM
            proj_dhcp_leases l
        WHERE
              l.ip = $1
          AND l.lease IS NOT NULL
          AND (l.agent_id IS NULL OR l.agent_id IN (SELECT aid FROM scope))
        UNION ALL
        SELECT
            l.lease
        FROM
            proj_dhcp_local_leases l
        WHERE
              l.ip = $1
          AND l.lease IS NOT NULL
          AND (l.agent_id IS NULL OR l.agent_id IN (SELECT aid FROM scope))
        UNION ALL
        SELECT
            d.mac
        FROM
            proj_discovered d
        WHERE
              d.discovered = $1
          AND d.mac IS NOT NULL
          AND (d.agent_id IS NULL OR d.agent_id IN (SELECT aid FROM scope))
        ) norm
WHERE
    length(norm.mac) = 12
