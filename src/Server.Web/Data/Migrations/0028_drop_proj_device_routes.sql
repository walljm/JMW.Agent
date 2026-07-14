-- proj_device_routes was write-only (materialized every ingest, read by no query). The
-- routing table now surfaces as a device-detail "Routes" fact view from facts_history.
DROP TABLE if EXISTS jmwdiscovery.proj_device_routes;
DELETE
FROM
    jmwdiscovery.retention_policies
WHERE
    table_name = 'proj_device_routes';
