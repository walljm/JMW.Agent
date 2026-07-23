-- InterfacesApi "speed" sort fix. The 0104 sort expression coalesce(speed_bps, -1) is bigint,
-- but the keyset cursor machinery compares the sort key against a text parameter — bigint > text
-- has no operator, so the speed sort failed at parse time on every page (including page 1, since
-- a row comparison type-checks even inside a NULL-guarded branch). The [SortableBy] migration
-- switches the expression to a zero-padded text form: speeds are non-negative, so 20-digit
-- zero-padding makes lexical order equal numeric order, and NULL maps to '' which sorts first
-- ascending — the same relative position the old -1 sentinel had.
CREATE INDEX IF NOT EXISTS proj_interfaces_speed_text_sort_idx
    ON proj_interfaces ((coalesce(lpad(speed_bps::text, 20, '0'), '')), device, interface);

-- The bigint expression index served only the broken query shape — no production SQL can
-- reference it anymore.
DROP INDEX IF EXISTS proj_interfaces_speed_sort_idx;
