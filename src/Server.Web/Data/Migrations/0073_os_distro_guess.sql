-- proj_systems.os_distro_guess: inferred OS distro from a curated, OS-exclusive signature
-- (SNMP sysDescr today — see docs/plans/vendor-derivation-updates.md §5). Kept as its own
-- column, not merged into `os_distro` — that path is written by real collectors (SSH,
-- Google Wifi, local OS collector) through the plain last-write-wins projection route, and a
-- guess re-derived every cycle (fresh collected_at each time) could otherwise silently clobber
-- an older, more authoritative value. Reporting queries should only fall back to this column
-- when `os_distro` is NULL.
ALTER TABLE proj_systems ADD COLUMN IF NOT EXISTS os_distro_guess TEXT;
