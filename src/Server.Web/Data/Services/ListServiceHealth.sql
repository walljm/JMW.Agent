-- Per-service health signal for the service_down sweep. CA services (proj_service_ca) expose an
-- explicit status ("running"/"stopped" — see StepCaCollector); every other service type has no
-- such signal today (DNS/HA collectors just emit nothing when unreachable, indistinguishable from
-- "briefly not polled this cycle"), so those fall back to staleness of proj_services.updated_at,
-- the one thing every service type touches on every successful poll.
SELECT
    ps.service
  , ps.type
  , ca.ca_status
  , ps.updated_at
FROM
    proj_services              ps
    LEFT JOIN proj_service_ca  ca
    ON ca.service = ps.service
