-- proj_device_certs and proj_device_trusted_cas were write-only (read by no query). They now
-- surface as device-detail "Certificates" and "Trusted CAs" fact views from facts_history.
-- DROP TABLE also removes proj_device_certs_expiry_idx.
DROP TABLE IF EXISTS jmwdiscovery.proj_device_certs;
DROP TABLE IF EXISTS jmwdiscovery.proj_device_trusted_cas;
DELETE FROM jmwdiscovery.retention_policies WHERE table_name IN ('proj_device_certs', 'proj_device_trusted_cas');
