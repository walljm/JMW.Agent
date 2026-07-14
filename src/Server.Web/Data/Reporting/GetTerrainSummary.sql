WITH dns    AS (
    SELECT
        count(*)                        AS dns_server_count
      , coalesce(sum(total_queries), 0) AS total_queries
      , coalesce(sum(total_blocked), 0) AS total_blocked
    FROM
        proj_dns_stats
    WHERE
         total_queries IS NOT NULL
      OR total_blocked IS NOT NULL
    )
   , scopes AS (
    SELECT
        count(*) FILTER (WHERE enabled = TRUE) AS active_scope_count
    FROM
        proj_dhcp_scopes
    )
   , leases AS (
    SELECT
        count(*) AS local_lease_count
    FROM
        proj_dhcp_local_leases
    )
   , known_cas AS (
    -- Certs we actually hold (operated CA services + trusted-on-host CA certs) — the only
    -- rows with a validity window, so the only ones "expiring" can apply to.
    SELECT root_subject_dn AS subject_dn, root_not_after AS not_after
    FROM proj_service_ca WHERE root_subject_dn IS NOT NULL

    UNION ALL

    SELECT int_subject_dn, int_not_after
    FROM proj_service_ca WHERE int_subject_dn IS NOT NULL

    UNION ALL

    SELECT max(subject_dn), max(not_after) FROM proj_device_certs WHERE is_ca = TRUE GROUP BY cert
    )
   , observed_cas AS (
    -- Issuers seen signing a cert but never captured themselves — same dedup rule as
    -- ListTerrainCaInventory.sql: suppressed once a real cert for that subject exists above.
    SELECT DISTINCT issuer_dn
    FROM (
        SELECT issuer_dn FROM proj_device_certs WHERE is_ca = FALSE AND issuer_dn IS NOT NULL
        UNION ALL
        SELECT tls_issuer FROM proj_discovered_tls WHERE tls_issuer IS NOT NULL
    ) sightings
    WHERE NOT EXISTS (SELECT 1 FROM known_cas k WHERE k.subject_dn = sightings.issuer_dn)
    )
   , cas AS (
    SELECT
        (SELECT count(*) FROM known_cas) + (SELECT count(*) FROM observed_cas) AS ca_count
      , (SELECT count(*) FROM known_cas WHERE not_after <= now() + INTERVAL '30 days')
            AS ca_expiring_count
    )
SELECT
    dns.dns_server_count
  , dns.total_queries
  , dns.total_blocked
  , scopes.active_scope_count
  , leases.local_lease_count
  , cas.ca_count
  , cas.ca_expiring_count
FROM
    dns
  , scopes
  , leases
  , cas
