-- Backfill agent_id on the two projections that feed obscured-MAC reconstruction, for legacy rows
-- that predate agent-id tracking.
--
-- Why they are stranded: agent_id rides along ONLY when a data column also changes -- GenericProjection's
-- ON CONFLICT ... DO UPDATE deliberately omits agent_id from its WHERE guard, and the EntityStateCache
-- drops entities whose columns are unchanged before they ever reach SQL. So a row created before agent-id
-- tracking whose data has been stable since -- e.g. a Google Wifi station's obscured_mac, which never
-- changes -- stays agent_id IS NULL forever, even though the polling agent keeps re-reporting it. (Rows
-- touched after the cutover carry the agent correctly; the INSERT path is not gated by the WHERE guard.)
--
-- Why that breaks merging: ObscuredMac reconstruction (DiscoveryMaterializer) looks up the real full MAC
-- for an obscured station by IP, scoped to the reporting agent via GetKnownMacsForIp ($2). A NULL reporter
-- matches only NULL candidates, so the real MAC an agent captured in ARP for the same IP (now stamped with
-- that agent's id) is filtered out -- the obscured station can never reconstruct its full MAC and never
-- merges with its ARP/scanner twin. Observed: 192.168.1.241 split into an obscured-only Google device and
-- its ARP-observed MAC (cca7c1...) that would otherwise unify.
--
-- Attribution rule: proj_discovered.device / proj_interfaces.device is the *reporting* device (the OnHub or
-- host that did the discovering / owns the interface). Every row a device reports belongs to the single
-- agent that polls that device, so a NULL row inherits its device's owning agent -- but only when that owner
-- is unambiguous (exactly one distinct non-null agent_id across the device's rows). A device with no
-- attributed agent at all is left NULL; we never guess. One-time and idempotent: after this runs no
-- unambiguous device has a NULL row left, and new rows INSERT with agent_id already set.

UPDATE proj_discovered d
SET agent_id = o.aid
FROM (
    SELECT
        device
      , (array_agg(DISTINCT agent_id) FILTER (WHERE agent_id IS NOT NULL))[1] AS aid
    FROM proj_discovered
    GROUP BY device
    HAVING count(DISTINCT agent_id) FILTER (WHERE agent_id IS NOT NULL) = 1
) o
WHERE d.device = o.device
  AND d.agent_id IS NULL
  AND o.aid IS NOT NULL;

UPDATE proj_interfaces d
SET agent_id = o.aid
FROM (
    SELECT
        device
      , (array_agg(DISTINCT agent_id) FILTER (WHERE agent_id IS NOT NULL))[1] AS aid
    FROM proj_interfaces
    GROUP BY device
    HAVING count(DISTINCT agent_id) FILTER (WHERE agent_id IS NOT NULL) = 1
) o
WHERE d.device = o.device
  AND d.agent_id IS NULL
  AND o.aid IS NOT NULL;
