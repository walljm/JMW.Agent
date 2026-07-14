-- oui_vendor(mac): resolve the IEEE-registered NIC vendor for a MAC address.
--
-- This is the single, reusable "projection-side join to the OUI table" used by
-- every report that surfaces a MAC. It performs a longest-prefix match against
-- oui_entries (MA-S 36-bit / MA-M 28-bit / MA-L 24-bit, i.e. 9/7/6 hex chars).
--
-- The MAC is normalized (separators stripped, lowercased) so callers may pass
-- any format: "aa:bb:cc:dd:ee:ff", "AABBCCDDEEFF", "aa-bb-cc-...".
--
-- Each probe uses fixed-length equality on prefix so the (prefix, bits) primary
-- key index serves the lookup — no sequential scan of oui_entries per row.
-- The vendor this returns is the NIC manufacturer, which is NOT necessarily the
-- device manufacturer; callers surface it in a dedicated "OUI" column.

CREATE OR REPLACE FUNCTION oui_vendor(mac text)
    RETURNS text
    LANGUAGE sql
    STABLE
    PARALLEL SAFE
AS $$
    WITH m AS (
        SELECT regexp_replace(lower(mac), '[^0-9a-f]', '', 'g') AS hex
    )
    SELECT vendor
    FROM (
        SELECT oe.vendor, oe.bits
        FROM oui_entries oe, m
        WHERE length(m.hex) >= 9 AND oe.bits = 36 AND oe.prefix = left(m.hex, 9)
        UNION ALL
        SELECT oe.vendor, oe.bits
        FROM oui_entries oe, m
        WHERE length(m.hex) >= 7 AND oe.bits = 28 AND oe.prefix = left(m.hex, 7)
        UNION ALL
        SELECT oe.vendor, oe.bits
        FROM oui_entries oe, m
        WHERE length(m.hex) >= 6 AND oe.bits = 24 AND oe.prefix = left(m.hex, 6)
    ) matches
    ORDER BY bits DESC
    LIMIT 1
$$;
