-- Resolves a service endpoint IP to the live device hosting it, for linking remotely-polled
-- services to their host (proj_services.device_id). Priority: the device's own interface IP
-- (strongest signal — it IS that host), then its last-seen IP, then an ARP neighbor sighting
-- (IP -> MAC -> fingerprinted device). Merged/alias devices are excluded via live_devices.
-- A no-match yields no rows so the caller leaves device_id NULL rather than guess a wrong host.
SELECT
    cand.device_id
FROM
    (
        SELECT
            i.device       AS device_id
          , 1              AS rank
          , i.updated_at   AS seen
        FROM
            proj_interfaces i
        WHERE
            split_part(i.ipv4, '/', 1) = $1
        UNION ALL
        SELECT
            s.device
          , 2
          , s.updated_at
        FROM
            proj_systems s
        WHERE
            s.last_seen_ip = $1
        UNION ALL
        SELECT
            d.device_id::text
          , 3
          , a.updated_at
        FROM
            proj_device_arp               a
            JOIN device_fingerprints df
            ON df.fp_type = 'mac'
                AND df.fp_value = a.mac
            JOIN devices             d
            ON d.device_id = df.device_id
        WHERE
            a.arp = $1
    ) cand
    JOIN live_devices ld
    ON ld.device_id::text = cand.device_id
ORDER BY
    cand.rank
  , cand.seen DESC NULLS LAST
    LIMIT 1
