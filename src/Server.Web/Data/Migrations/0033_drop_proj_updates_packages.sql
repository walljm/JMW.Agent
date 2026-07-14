-- proj_updates (update-count summary) and proj_packages (installed package list) were
-- write-only (read by no query). They now surface as device-detail "Software Updates" and
-- "Packages" fact views from facts_history (pending-update detail is its own "Pending
-- Updates" view).
DROP TABLE IF EXISTS jmwdiscovery.proj_updates;
DROP TABLE IF EXISTS jmwdiscovery.proj_packages;
DELETE FROM jmwdiscovery.retention_policies WHERE table_name IN ('proj_updates', 'proj_packages');
