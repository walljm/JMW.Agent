-- Remove hostname aliases that the new normalization rules would reject:
-- Docker-internal names, localhost variants, and generic OS defaults.
-- These were stored before hostname.Normalize() was introduced and will
-- never be refreshed (the pipeline now rejects them on ingestion), so
-- they must be purged explicitly to let better names win on priority.
DELETE FROM hostname_aliases
WHERE hostname IN (
    'host.docker.internal',
    'gateway.docker.internal',
    'localhost',
    'localdomain',
    'local',
    'ubuntu',
    'debian',
    'raspberrypi',
    'kali',
    'openwrt',
    'homeassistant',
    'android',
    'linux',
    'windows',
    'router',
    'gateway'
)
OR hostname LIKE '%.docker.internal';

-- Also deduplicate interface_addresses: drop rows where the same bare IP
-- exists as both "x.x.x.x" and "x.x.x.x/prefix" for the same interface.
-- Keep the bare-IP row (shorter); delete the CIDR duplicate.
DELETE FROM interface_addresses
WHERE rowid NOT IN (
    SELECT MIN(rowid)
    FROM interface_addresses
    GROUP BY interface_id,
             CASE WHEN instr(address, '/') > 0
                  THEN substr(address, 1, instr(address, '/') - 1)
                  ELSE address
             END,
             family
);
