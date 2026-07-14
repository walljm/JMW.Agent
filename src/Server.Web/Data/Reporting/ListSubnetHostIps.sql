-- Candidate host IPs for subnet membership counting (Subnets page). Pulled from every
-- projection that carries an IPv4 address; deduped and restricted to dotted-quad values so a
-- stray hostname/IPv6 value in one of these text columns can't slip through. v1 is IPv4-only —
-- ipv6 subnet coverage is deferred (see scratch/topology.md §6).
SELECT DISTINCT
    ip
FROM
    (
    SELECT
        split_part(ipv4, '/', 1) AS ip
    FROM
        proj_interfaces
    WHERE
        ipv4 IS NOT NULL
      AND ipv4 <> ''
    UNION
    SELECT
        arp AS ip
    FROM
        proj_device_arp
    WHERE
        arp IS NOT NULL
      AND arp <> ''
    UNION
    SELECT
        ip
    FROM
        proj_dhcp_leases
    WHERE
        ip IS NOT NULL
      AND ip <> ''
    UNION
    SELECT
        discovered AS ip
    FROM
        proj_discovered
    WHERE
        discovered IS NOT NULL
      AND discovered <> ''
    ) all_ips
WHERE
    ip ~ '^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}$'
