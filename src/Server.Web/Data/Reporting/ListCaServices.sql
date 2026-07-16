-- The Certificate Authority services operating in the network — one row per CA service
-- (proj_service_ca), resolved to the host device it runs on. This is deliberately narrower
-- than ListTerrainCaInventory.sql: that unifies every CA *certificate* known anywhere (operated
-- + trusted-on-host + observed-in-traffic); this lists the actual CAs we run/observe as
-- services (the device acting as the CA — step-ca today), with its root/intermediate chain,
-- status, address, and provisioner count.
--
-- Host resolution: a loopback service is minted with its host's DeviceId as the ServiceId
-- (AGENTS.md, "Device vs service identity"), so proj_systems joins on device = service. A
-- remote CA service whose id isn't a device id simply shows no host.
SELECT
    sc.service                                    AS service_ref
  , COALESCE(sys.friendly_name, sys.hostname)     AS host_name
  , sc.ca_status                                  AS status
  , sc.ca_address                                 AS address
  , sc.root_subject_dn                            AS root_subject_dn
  , sc.root_not_before                            AS root_not_before
  , sc.root_not_after                             AS root_not_after
  , sc.root_fingerprint                           AS root_fingerprint
  , sc.int_subject_dn                             AS int_subject_dn
  , sc.int_not_before                             AS int_not_before
  , sc.int_not_after                              AS int_not_after
  , (
        SELECT count(*)
        FROM proj_service_ca_provisioners p
        WHERE p.service = sc.service
    )                                             AS provisioner_count
FROM
    proj_service_ca              sc
    LEFT JOIN proj_systems sys ON sys.device = sc.service
WHERE
    (
        $1::text IS NULL
        OR sc.root_subject_dn ILIKE '%' || $1 || '%'
        OR sc.int_subject_dn ILIKE '%' || $1 || '%'
        OR sc.ca_status ILIKE '%' || $1 || '%'
        OR sys.hostname ILIKE '%' || $1 || '%'
    )
ORDER BY
    COALESCE(sys.hostname, sc.service)
