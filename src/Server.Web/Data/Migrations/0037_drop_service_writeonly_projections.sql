-- proj_home_assistant, proj_home_assistant_addons and proj_service_ca_dns_names were
-- write-only (read by no query). Service-keyed data now surfaces as service-detail fact
-- views (Home Assistant, Add-ons, CA DNS Names) rendered from facts_history — the same path
-- also gives DNS top-N (queried/blocked/clients) a home, which never had a projection.
DROP TABLE IF EXISTS jmwdiscovery.proj_home_assistant;
DROP TABLE IF EXISTS jmwdiscovery.proj_home_assistant_addons;
DROP TABLE IF EXISTS jmwdiscovery.proj_service_ca_dns_names;
DELETE FROM jmwdiscovery.retention_policies
WHERE table_name IN ('proj_home_assistant', 'proj_home_assistant_addons', 'proj_service_ca_dns_names');
