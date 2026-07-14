-- Every distinct Certificate Authority known anywhere in the fleet, unified from three signal
-- strengths:
--   operated            — a CA service we run (proj_service_ca root/intermediate; step-ca today)
--   trusted-on-host      — a CA cert (is_ca) found in some host's trust store or config dir
--                          (proj_device_certs), grouped by fingerprint since the same root/
--                          intermediate is often distributed to many devices
--   observed-in-traffic — an issuer DN seen signing a cert but never captured itself, from a
--                          non-CA cert's issuer_dn (proj_device_certs) or a TLS-probed leaf's
--                          issuer (proj_discovered_tls). Suppressed wherever that issuer DN
--                          already matches a subject_dn we have a real cert for above.
WITH operated AS (
    SELECT
        'operated'::text          AS kind
      , 'root'::text              AS subtype
      , root_subject_dn           AS subject_dn
      , root_fingerprint          AS fingerprint
      , root_not_before           AS not_before
      , root_not_after            AS not_after
      , service                   AS service_ref
      , NULL::bigint              AS seen_on_count
    FROM
        proj_service_ca
    WHERE
        root_subject_dn IS NOT NULL

    UNION ALL

    SELECT
        'operated'
      , 'intermediate'
      , int_subject_dn
      , NULL::text
      , int_not_before
      , int_not_after
      , service
      , NULL::bigint
    FROM
        proj_service_ca
    WHERE
        int_subject_dn IS NOT NULL
    )
   , trusted AS (
    SELECT
        cert                     AS fingerprint
      , max(subject_dn)          AS subject_dn
      , min(not_before)          AS not_before
      , max(not_after)           AS not_after
      , count(DISTINCT device)   AS seen_on_count
    FROM
        proj_device_certs
    WHERE
        is_ca = TRUE
    GROUP BY
        cert
    )
   , known_subjects AS (
    SELECT subject_dn FROM operated WHERE subject_dn IS NOT NULL
    UNION
    SELECT subject_dn FROM trusted WHERE subject_dn IS NOT NULL
    )
   , issuer_sightings AS (
    SELECT device, issuer_dn FROM proj_device_certs
    WHERE is_ca = FALSE AND issuer_dn IS NOT NULL

    UNION ALL

    SELECT device, tls_issuer AS issuer_dn FROM proj_discovered_tls
    WHERE tls_issuer IS NOT NULL
    )
   , observed AS (
    SELECT
        s.issuer_dn              AS issuer_dn
      , count(DISTINCT s.device) AS seen_on_count
    FROM
        issuer_sightings s
    WHERE
        NOT EXISTS (SELECT 1 FROM known_subjects k WHERE k.subject_dn = s.issuer_dn)
    GROUP BY
        s.issuer_dn
    )
   , all_cas AS (
    SELECT
        kind
      , subtype
      , subject_dn
      , fingerprint
      , not_before
      , not_after
      , service_ref
      , seen_on_count
    FROM
        operated

    UNION ALL

    SELECT
        'trusted-on-host'
      , NULL::text
      , subject_dn
      , fingerprint
      , not_before
      , not_after
      , NULL::text
      , seen_on_count
    FROM
        trusted

    UNION ALL

    SELECT
        'observed-in-traffic'
      , NULL::text
      , issuer_dn
      , NULL::text
      , NULL::timestamptz
      , NULL::timestamptz
      , NULL::text
      , seen_on_count
    FROM
        observed
    )
SELECT
    kind
  , subtype
  , subject_dn
  , fingerprint
  , not_before
  , not_after
  , service_ref
  , seen_on_count
FROM
    all_cas
WHERE
    $1::text IS NULL
    OR subject_dn ILIKE '%' || $1 || '%'
    OR fingerprint ILIKE '%' || $1 || '%'
    OR service_ref ILIKE '%' || $1 || '%'
ORDER BY
    (CASE kind WHEN 'operated' THEN 0 WHEN 'trusted-on-host' THEN 1 ELSE 2 END)
  , subject_dn NULLS LAST
