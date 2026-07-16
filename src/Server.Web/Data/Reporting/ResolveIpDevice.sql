-- Resolves a single IP (typically a subnet gateway) to its MAC and, if fingerprinted, its
-- known device — same join shape as ListArp.sql, scoped to one address.
SELECT
    a.mac
  , CASE
        WHEN df.device_id IS NULL
            THEN NULL
        ELSE d.device_id
    END               AS resolved_device_id
  , COALESCE(rs.friendly_name, rs.hostname) AS resolved_hostname
  , oui_vendor(a.mac) AS oui
FROM
    proj_device_arp               a
    LEFT JOIN device_fingerprints df
    ON df.fp_type = 'mac'
        AND df.fp_value = a.mac
    LEFT JOIN devices             d
    ON d.device_id = df.device_id
    LEFT JOIN proj_systems        rs
    ON rs.device = d.device_id::text
WHERE
    a.arp = $1
    LIMIT 1
