-- Resolves a MAC address directly to its known device — no ARP hop needed. Used when an LLDP
-- neighbor's remote identity is only a chassis MAC (no management IP was advertised).
SELECT
    d.device_id AS resolved_device_id
  , COALESCE(rs.friendly_name, rs.hostname) AS resolved_hostname
FROM
    device_fingerprints            df
    JOIN devices                   d
    ON d.device_id = df.device_id
    LEFT JOIN proj_systems         rs
    ON rs.device = d.device_id::text
WHERE
    df.fp_type = 'mac'
    AND df.fp_value = $1
    LIMIT 1
