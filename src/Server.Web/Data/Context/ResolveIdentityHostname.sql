-- Context derivation "identity-hostname" (docs/plans/context-derivations.md §4): the current
-- real OS hostname per device, straight from proj_systems. Reading the TABLE (not facts) covers
-- all three hostname write paths in one place — the ingest router, DiscoveryMaterializer's
-- EmitPromotedIntrinsics, and the raw UpsertDeviceSystem upsert (Home Assistant promotion),
-- the last of which never writes a fact at all.
SELECT
    s.device
  , s.hostname AS value
FROM
    proj_systems s
WHERE
    s.hostname IS NOT NULL
