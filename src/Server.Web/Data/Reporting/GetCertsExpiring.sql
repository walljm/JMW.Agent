-- Service CA certificates expiring within 30 days. The one Needs-Attention signal not yet
-- migrated to the incidents table (see IncidentTypeRegistry's remarks on cert_expiring).
SELECT
    count(*) AS certs_expiring
FROM
    proj_service_ca
WHERE
     (root_not_after IS NOT NULL AND root_not_after <= now() + INTERVAL '30 days')
  OR (int_not_after IS NOT NULL AND int_not_after <= now() + INTERVAL '30 days')
