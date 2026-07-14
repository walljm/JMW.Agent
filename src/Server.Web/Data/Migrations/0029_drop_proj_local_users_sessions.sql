-- proj_local_users and proj_sessions were write-only (read by no query). They now surface
-- as device-detail "Users" and "Active Sessions" fact views from facts_history.
DROP TABLE IF EXISTS jmwdiscovery.proj_local_users;
DROP TABLE IF EXISTS jmwdiscovery.proj_sessions;
DELETE FROM jmwdiscovery.retention_policies WHERE table_name IN ('proj_local_users', 'proj_sessions');
