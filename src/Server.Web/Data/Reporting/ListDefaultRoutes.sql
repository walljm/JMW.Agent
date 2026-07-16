-- Each device's IPv4 default-route gateway. Used two ways on the Subnets page: (1) fills a
-- subnet's Gateway when no DHCP scope provides one, and (2) the L3 graph's sole signal for an
-- edge that leads off the observed network entirely (the gateway IP doesn't fall inside any
-- known subnet) — the "Internet" node.
SELECT
    r.device
  , COALESCE(s.friendly_name, s.hostname) AS hostname
  , r.gateway
FROM
    proj_device_routes    r
    LEFT JOIN proj_systems s
    ON s.device = r.device
WHERE
    r.route = '0.0.0.0/0'
  AND r.gateway IS NOT NULL
  AND r.gateway <> ''
