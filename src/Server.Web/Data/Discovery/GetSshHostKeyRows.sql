-- Discovered rows that carry an SSH host-key fingerprint. The materializer resolves
-- each as a stable per-host identity (unioned with the row's MAC when present).
-- SshHostKey read from materialization_facts (docs/plans/architecture-identity-facts.md §5
-- Phase 2b); mac stays on proj_discovered (§2).
SELECT
    d.mac
  , idf.value AS ssh_host_key
FROM
    materialization_facts idf
JOIN proj_discovered d ON d.device = idf.device AND d.discovered = idf.entity_key
WHERE
    idf.attribute_path = 'Device[].Discovered[].SshHostKey'
