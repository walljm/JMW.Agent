-- Context derivation "identity-ip" (docs/plans/context-derivations.md §4): the best identity IP
-- per device. Set form of DeviceListApi's former ip_best lateral — four candidate sources,
-- filtered to identity-quality addresses (ip_identity_rank < 99), preferring IPv4 over IPv6,
-- LAN over public rank, then recency. Guards kept verbatim from the lateral original:
-- pdi.obscured_mac IS NULL prevents a reconstructed-MAC sighting smearing another identity's IP
-- onto this row. MAC-keyed joins are globally safe (MACs are globally unique) — the agent-scope
-- rule applies to IP->MAC joins, not MAC->IP (docs/plans/ha-device-enrichment.md §5).
--
-- One deliberate improvement over the lateral original: the last_seen_ip candidate's recency is
-- s.updated_at (when that IP claim was last written) rather than the device's overall
-- fingerprint recency — the device being freshly seen said nothing about how fresh its frozen
-- last_seen_ip was, which could out-rank a genuinely newer ARP sighting after a DHCP move.
WITH newest_mac AS (
    SELECT DISTINCT ON (device_id)
        device_id
      , fp_value
    FROM
        device_fingerprints
    WHERE
        fp_type = 'mac'
    ORDER BY
        device_id
      , last_seen DESC
    )
  , cand AS (
    SELECT
        i.device
      , split_part(i.ipv4, '/', 1) AS ip
      , i.updated_at AS seen
    FROM
        proj_interfaces i
    WHERE
        i.ipv4 IS NOT NULL
    UNION ALL
    SELECT
        s.device
      , s.last_seen_ip
      , s.updated_at
    FROM
        proj_systems s
    WHERE
        s.last_seen_ip IS NOT NULL
    UNION ALL
    SELECT
        nm.device_id::text
      , a.arp
      , a.updated_at
    FROM
        newest_mac           nm
        JOIN proj_device_arp a
        ON a.mac = nm.fp_value
    UNION ALL
    SELECT
        nm.device_id::text
      , pdi.discovered
      , pdi.updated_at
    FROM
        newest_mac           nm
        JOIN proj_discovered pdi
        ON pdi.mac = nm.fp_value
    WHERE
        pdi.obscured_mac IS NULL
      AND pdi.discovered IS NOT NULL
    )
SELECT DISTINCT ON (cand.device)
    cand.device
  , cand.ip AS value
FROM
    cand
WHERE
    cand.ip IS NOT NULL
  AND ip_identity_rank(cand.ip) < 99
ORDER BY
    cand.device
  , (cand.ip LIKE '%:%')
  , ip_identity_rank(cand.ip)
  , cand.seen DESC NULLS LAST
