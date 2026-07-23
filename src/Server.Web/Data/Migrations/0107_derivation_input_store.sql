-- Derivation-input current-value store (docs/plans/context-derivations.md §6.5, decided with
-- Boss 2026-07-23): materialization_facts becomes the hydration source for derivation inputs —
-- AnalysisEngine's hydratable input paths are routed here (DerivationInputProjection) and
-- FactRepository.HydrateInputsAsync reads this table instead of facts_history's latest-per-id.
-- Why: facts_history is retention-pruned, so an unchanged-but-true fact's newest row can vanish
-- until an agent re-sends full state — hydration then silently misses it (the gap
-- DeviceRecollectSweepService papers over). A current-value row upserted in place has no such
-- window: derivations become retention-proof, and a new derivation input is a data change (one
-- more routed path), never a column migration. Bounded: rows = devices x input-paths.

-- Typed values: derivation inputs include numerics (SystemMemUsedBytes/TotalBytes feed the
-- mem-percent fan-in), unlike the string-only identity signals this table was built for.
-- kind mirrors FactValueKind (Null=0, String=1, Long=2, Double=3, Bool=4); value keeps its
-- NOT NULL text contract (numeric rows store the canonical text rendering for readability),
-- value_long/value_double carry the typed payload hydration reconstructs from.
ALTER TABLE materialization_facts ADD COLUMN IF NOT EXISTS kind smallint NOT NULL DEFAULT 1;
ALTER TABLE materialization_facts ADD COLUMN IF NOT EXISTS value_long bigint;
ALTER TABLE materialization_facts ADD COLUMN IF NOT EXISTS value_double float8;

-- Retention split. This table's policy (steady tier, 30d since migration 0101) exists for
-- neighbor-sighting identity rows (entity_key = the discovered IP, same lifetime as
-- proj_discovered). Device-scoped derivation inputs (entity_key = '') are device-lifetime
-- state — pruning them would reintroduce the exact staleness this store eliminates.
-- prune_predicate scopes a policy's DELETE to matching rows.
ALTER TABLE retention_policies ADD COLUMN IF NOT EXISTS prune_predicate text;

COMMENT ON COLUMN retention_policies.prune_predicate IS
    'Optional raw-SQL predicate ANDed into the prune DELETE. Migration-seeded only — never user '
    'input (defense-in-depth regex in RetentionService, same posture as table/column names).';

UPDATE retention_policies
SET prune_predicate = 'entity_key <> '''''
  , notes = 'Discovered-neighbor rows (entity_key = neighbor IP) prune on the steady tier; '
            'device-scoped derivation-input rows (entity_key = '''') are permanent current-value '
            'state and never prune (0107).'
WHERE table_name = 'materialization_facts';
