-- Best current IP for a MAC ($1, canonical bare 12-hex lowercase), scoped to LAN $2 (the target's
-- agent). Used to resolve a mac-kind target to a concrete address at config-assembly time so the
-- address follows DHCP moves instead of being pinned to a stale IP.
--
-- Inverse of GetKnownMacsForIp.sql (same MAC↔IP sources + agent-scoping rationale: RFC1918
-- addresses are reused across independent LANs, so a row with a *different* known agent_id is
-- excluded; a null agent_id from before scoping existed is treated as unscoped). The IP-ranking
-- mirrors GetDeviceSummary.sql: IPv4 before IPv6, private LAN before public, then most-recently
-- seen so a fresh ARP/DHCP sighting wins over a stale one. Loopback/link-local/unspecified
-- (ip_identity_rank >= 99) are never returned.
SELECT
    cand.ip
FROM
    (
        SELECT
            a.arp AS ip
          , a.updated_at AS seen
        FROM
            proj_device_arp a
        WHERE
              a.mac = $1
          AND a.arp IS NOT NULL
          AND (a.agent_id IS NULL OR a.agent_id = $2)
        UNION ALL
        SELECT
            l.ip
          , l.updated_at
        FROM
            proj_dhcp_leases l
        WHERE
              l.lease = $1
          AND l.ip IS NOT NULL
          AND (l.agent_id IS NULL OR l.agent_id = $2)
        UNION ALL
        SELECT
            l.ip
          , l.updated_at
        FROM
            proj_dhcp_local_leases l
        WHERE
              l.lease = $1
          AND l.ip IS NOT NULL
          AND (l.agent_id IS NULL OR l.agent_id = $2)
        UNION ALL
        SELECT
            d.discovered
          , d.updated_at
        FROM
            proj_discovered d
        WHERE
              d.mac = $1
          AND d.discovered IS NOT NULL
          AND (d.agent_id IS NULL OR d.agent_id = $2)
        ) cand
WHERE
    cand.ip IS NOT NULL
  AND ip_identity_rank(cand.ip) < 99
ORDER BY
    (cand.ip LIKE '%:%')       -- IPv4 before IPv6
  , ip_identity_rank(cand.ip)  -- private LAN before public/WAN
  , cand.seen DESC NULLS LAST  -- most recently seen IP wins (follows DHCP moves)
LIMIT 1
