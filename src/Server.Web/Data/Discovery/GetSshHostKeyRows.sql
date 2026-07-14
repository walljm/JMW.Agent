-- Discovered rows that carry an SSH host-key fingerprint. The materializer resolves
-- each as a stable per-host identity (unioned with the row's MAC when present).
SELECT
    mac
  , ssh_host_key
FROM
    proj_discovered
WHERE
    ssh_host_key IS NOT NULL
