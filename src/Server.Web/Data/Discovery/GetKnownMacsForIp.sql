-- Real full MACs the server already knows for a given IP ($1), scoped to the LAN of agent $2 (the
-- calling/reporting agent). The MAC columns are stored in the canonical bare 12-hex lowercase form
-- (normalized at ingest), so these read directly. The length = 12 filter drops any non-full value
-- (e.g. an obscured MAC that lost its '*').
--
-- Scoping rationale: RFC1918 addresses are commonly reused across independent LANs/sites this
-- server ingests from (one operator can run agents at more than one physical location). Without
-- scoping, an IP match here can pair a MAC observed on a completely different network — see
-- docs/plans/ha-device-enrichment.md §5. Each source table carries its own agent_id (see
-- ProjectionDef.TracksAgentId), populated going forward by GenericProjection's normal write
-- path — rows written before that started (agent_id IS NULL) are treated as unscoped rather
-- than excluded, so existing installations don't lose recall on their pre-migration data; a
-- row with a *different*, known agent_id is admitted only when co-located (below).
--
-- Same-LAN, cross-agent recall: the scope is not agent $2 alone but agent $2 PLUS every agent
-- proven co-located with it (agent_colocation — they share >= 3 globally-unique ARP MACs, i.e. one
-- L2 broadcast domain). This is what lets a MAC captured in ARP by one agent reconstruct an
-- obscured Google Wifi station polled by a *different* same-LAN agent, without reintroducing the
-- cross-site collision the scoping prevents (co-location is proven by globally-unique MACs, which
-- do not repeat across sites the way RFC1918 IPs do). A NULL reporter yields a single NULL scope
-- row, so it still matches only unscoped (NULL-agent_id) candidates — unchanged from before.
WITH scope AS (
    SELECT $2::uuid AS aid
    UNION
    SELECT other_agent FROM agent_colocation WHERE agent = $2
)
SELECT DISTINCT
    norm.mac
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
