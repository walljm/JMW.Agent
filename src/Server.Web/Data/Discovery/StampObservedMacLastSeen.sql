-- Passive-device liveness. The materializer resolves only NEW MACs (GetNewArpMacs et al. anti-join
-- fingerprinted_macs), so a device already fingerprinted and seen only via passive ARP/DHCP/discovery
-- never re-resolves and its device_fingerprints.last_seen would freeze -- after which the liveness
-- window (visible_devices) wrongly hides it despite it being present.
--
-- This advances last_seen for every EXISTING mac fingerprint to the freshest time that MAC was
-- observed across the presence projections. It only ever moves last_seen FORWARD (observed.seen >
-- df.last_seen), to a timestamp that is itself a real observation -- so a present device (whose
-- ephemeral ARP is re-sent every cycle, keeping its observed updated_at current) stays fresh, while
-- a DEPARTED device -- whose observation timestamp is frozen at its last sighting -- is never bumped
-- and ages out of the window normally. MAC columns are canonical bare 12-hex, matching fp_value.
--
-- devices.last_seen (the denormalized max-sighting column, context-derivations.md §3.4) is bumped
-- in the same statement so the two can never drift: this and UpsertFingerprintsAsync are the only
-- two places fingerprint last_seen moves.
WITH observed AS (
    SELECT
        mac
      , max(updated_at) AS seen
    FROM
        (
            SELECT mac, updated_at FROM proj_device_arp WHERE mac IS NOT NULL
            UNION ALL
            SELECT lease, updated_at FROM proj_dhcp_local_leases WHERE lease IS NOT NULL
            UNION ALL
            SELECT lease, updated_at FROM proj_dhcp_leases WHERE lease IS NOT NULL
            UNION ALL
            SELECT mac, updated_at FROM proj_discovered WHERE mac IS NOT NULL
        ) s
    GROUP BY
        mac
    )
  , bumped AS (
    UPDATE device_fingerprints df
    SET
        last_seen = observed.seen
    FROM
        observed
    WHERE
          df.fp_type = 'mac'
      AND df.fp_value = observed.mac
      AND observed.seen > df.last_seen RETURNING df.device_id, df.last_seen
    )
  , device_bump AS (
    UPDATE devices d
    SET
        last_seen = GREATEST(d.last_seen, b.seen)
    FROM
        (SELECT device_id, max(last_seen) AS seen FROM bumped GROUP BY device_id) b
    WHERE
        d.device_id = b.device_id
    )
SELECT
    device_id::text AS device
FROM
    bumped;
