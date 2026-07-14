INSERT INTO proj_systems
(
      device
    , hostname
    , last_seen_ip
    , os_family
    , updated_at
)
VALUES
    (
          $1
        , $2
        , $3
        , $4
        , now()
    ) ON CONFLICT (device) DO
UPDATE set
    hostname = COALESCE (proj_systems.hostname, EXCLUDED.hostname),
    -- Track the CURRENT IP: a fresh sighting's IP wins so the value follows DHCP
    -- moves. Only keep the prior IP when this sighting carries none (e.g. a
    -- hostname-only bootstrap passes last_seen_ip = NULL).
    last_seen_ip = COALESCE (EXCLUDED.last_seen_ip, proj_systems.last_seen_ip),
    -- Discovered OS is a fallback: a device-collected os_family (set elsewhere) is never
    -- overwritten by the HTTP-fingerprint guess.
    os_family = COALESCE (proj_systems.os_family, EXCLUDED.os_family),
    updated_at = now()
    RETURNING device
