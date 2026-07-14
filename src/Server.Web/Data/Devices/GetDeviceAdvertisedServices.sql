-- Distinct mDNS/Bonjour service types this device advertises, gathered across every
-- observer that saw it (proj_discovered_services keyed by observer + neighbor IP,
-- joined to this device via its reconstructed MAC fingerprint).
SELECT DISTINCT
    svc.service
FROM
    proj_discovered_services svc
    JOIN proj_discovered     d
    ON d.device = svc.device AND d.discovered = svc.discovered
    JOIN device_fingerprints f
    ON f.fp_type = 'mac' AND f.fp_value = d.mac AND f.device_id = $1
WHERE
    svc.service IS NOT NULL
ORDER BY
    svc.service
