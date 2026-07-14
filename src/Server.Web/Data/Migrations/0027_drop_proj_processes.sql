-- proj_processes was write-only: materialized every ingest, read by no query. Its data
-- (top processes per device) now surfaces as a device-detail "Processes" fact view rendered
-- from facts_history, so the projection is redundant. Drop it and its retention policy.
DROP TABLE if EXISTS jmwdiscovery.proj_processes;
DELETE
FROM
    jmwdiscovery.retention_policies
WHERE
    table_name = 'proj_processes';
