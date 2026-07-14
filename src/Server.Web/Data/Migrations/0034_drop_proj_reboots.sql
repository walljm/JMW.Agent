-- proj_reboots (last-boot / 30-day count) and proj_reboots_history (boot events) were
-- write-only (read by no query). They now surface as device-detail "Reboots" and
-- "Reboot History" fact views from facts_history.
DROP TABLE if EXISTS jmwdiscovery.proj_reboots;
DROP TABLE if EXISTS jmwdiscovery.proj_reboots_history;
DELETE
FROM
    jmwdiscovery.retention_policies
WHERE
    table_name IN ('proj_reboots', 'proj_reboots_history');
