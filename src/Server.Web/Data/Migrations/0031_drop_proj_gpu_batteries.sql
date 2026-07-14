-- proj_gpu and proj_batteries were write-only (read by no query). They now surface as
-- device-detail "GPU" and "Battery" fact views from facts_history.
DROP TABLE if EXISTS jmwdiscovery.proj_gpu;
DROP TABLE if EXISTS jmwdiscovery.proj_batteries;
DELETE
FROM
    jmwdiscovery.retention_policies
WHERE
    table_name IN ('proj_gpu', 'proj_batteries');
