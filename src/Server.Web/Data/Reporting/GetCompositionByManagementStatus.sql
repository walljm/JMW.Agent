-- Live-device counts by management status (managed vs discovered), descending. Single-table:
-- management_status lives on devices (via live_devices d.*). Mirrors GetNetworkSummary's
-- managed/discovered split so the composition card agrees with the Totals tiles.
SELECT
    management_status
  , count(*) AS COUNT
FROM
    live_devices
GROUP BY
    management_status
ORDER BY
    COUNT              DESC
  , management_status ASC
