-- proj_snmp_device and proj_docker were write-only (read by no query). They now surface as
-- device-detail "SNMP" and "Docker" fact views from facts_history.
DROP TABLE if EXISTS jmwdiscovery.proj_snmp_device;
DROP TABLE if EXISTS jmwdiscovery.proj_docker;
DELETE
FROM
    jmwdiscovery.retention_policies
WHERE
    table_name IN ('proj_snmp_device', 'proj_docker');
