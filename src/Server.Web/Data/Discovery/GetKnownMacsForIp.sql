-- Real full MACs the server already knows for a given IP ($1). The MAC columns are stored in the
-- canonical bare 12-hex lowercase form (normalized at ingest), so these read directly. Sources that
-- carry IP↔MAC: the ARP cache, both DHCP-lease projections, and prior non-obscured discovery.
-- The length = 12 filter drops any non-full value (e.g. an obscured MAC that lost its '*').
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
        UNION ALL
        SELECT
            l.lease
        FROM
            proj_dhcp_leases l
        WHERE
              l.ip = $1
          AND l.lease IS NOT NULL
        UNION ALL
        SELECT
            l.lease
        FROM
            proj_dhcp_local_leases l
        WHERE
              l.ip = $1
          AND l.lease IS NOT NULL
        UNION ALL
        SELECT
            d.mac
        FROM
            proj_discovered d
        WHERE
              d.discovered = $1
          AND d.mac IS NOT NULL
        ) norm
WHERE
    length(norm.mac) = 12
