-- proj_device_services was write-only (read by no query). Failed/degraded units now surface
-- as a device-detail "System Services" fact view from facts_history.
DROP TABLE if EXISTS jmwdiscovery.proj_device_services;
DELETE
FROM
    jmwdiscovery.retention_policies
WHERE
    table_name = 'proj_device_services';
