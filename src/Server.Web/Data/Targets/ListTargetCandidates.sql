-- Discovered IPs/services that look collectable by an existing collector type, excluding
-- any (endpoint, collector_type) pair already configured as a target for this agent.
--
-- Signal per collector type (best-effort — a candidate is a suggestion, not a guarantee the
-- collector is actually reachable/authenticated):
--   ssh:             SshBannerScanner recorded a banner, or a host key was captured.
--   snmp:            SnmpBroadcastScanner or GatewaySnmpArpScanner got a reply.
--   cert:            TlsCertScanner pulled a certificate on 443.
--   google-wifi:     vendor looks like Google (OnHub/Nest Wifi/Google Wifi access points).
--                    No network scanner currently fingerprints this reliably — vendor is
--                    the best signal available until a dedicated signature exists.
--   home-assistant:  mDNS advertised "_home-assistant._tcp" (passive signature). Endpoint
--                    is synthesized as the default HA port, not discovered directly.
--                    technitium-dns has no known passive discovery signature, so there is
--                    no candidate source for it.
--
-- `sources` is a comma-joined list of scanner names (no per-item spacing — see
-- NetworkDiscoveryCollector's write side), so it's split into an array and tested for exact
-- token membership (&&) rather than substring LIKE — a future scanner whose name happens to
-- contain another scanner's name as a substring (e.g. a "SnmpBroadcastScannerV2") would
-- otherwise false-positive under LIKE.
SELECT
    d.discovered AS endpoint,
    v.collector_type,
    d.hostname,
    d.vendor,
    d.model
FROM
    proj_discovered d
    CROSS JOIN LATERAL (
        VALUES
            ('ssh', (string_to_array(d.sources, ',') && ARRAY['SshBannerScanner'])
                OR EXISTS (
                    SELECT 1 FROM materialization_facts f
                    WHERE f.device = d.device AND f.entity_key = d.discovered
                      AND f.attribute_path = 'Device[].Discovered[].SshHostKey'
                )),
            ('snmp', string_to_array(d.sources, ',')
                && ARRAY['SnmpBroadcastScanner', 'GatewaySnmpArpScanner']),
            ('cert', string_to_array(d.sources, ',') && ARRAY['TlsCertScanner']),
            ('google-wifi', d.vendor ILIKE 'Google%')
    ) AS v (collector_type, matches)
WHERE
    v.matches
    AND NOT EXISTS (
        SELECT
            1
        FROM
            targets t
        WHERE
            t.agent_id = $1
            AND t.endpoint = d.discovered
            AND t.collector_type = v.collector_type
    )

UNION ALL

SELECT
    'http://' || d.discovered || ':8123' AS endpoint,
    'home-assistant' AS collector_type,
    d.hostname,
    d.vendor,
    d.model
FROM
    proj_discovered_services s
    JOIN proj_discovered d ON d.device = s.device
        AND d.discovered = s.discovered
WHERE
    s.service = '_home-assistant._tcp'
    AND NOT EXISTS (
        SELECT
            1
        FROM
            targets t
        WHERE
            t.agent_id = $1
            AND t.collector_type = 'home-assistant'
            AND t.endpoint LIKE '%' || d.discovered || '%'
    )

ORDER BY
    collector_type,
    endpoint
