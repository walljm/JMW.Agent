-- Adds the ISO 3166-1 alpha-2 country extracted from the IEEE registry's free-text
-- "Organization Address" field (OuiUpdateService.ExtractCountryCode), and a matching
-- lookup function mirroring oui_vendor(mac)'s longest-prefix-match shape. Kept as a
-- separate function (rather than changing oui_vendor's return shape) so every existing
-- caller of oui_vendor is unaffected.

ALTER TABLE oui_entries ADD COLUMN country text;

CREATE OR REPLACE FUNCTION oui_country(mac text)
    RETURNS text
    LANGUAGE sql
    STABLE
    PARALLEL SAFE
AS $$
    WITH m AS (
        SELECT regexp_replace(lower(mac), '[^0-9a-f]', '', 'g') AS hex
    )
    SELECT country
    FROM (
        SELECT oe.country, oe.bits
        FROM oui_entries oe, m
        WHERE length(m.hex) >= 9 AND oe.bits = 36 AND oe.prefix = left(m.hex, 9)
        UNION ALL
        SELECT oe.country, oe.bits
        FROM oui_entries oe, m
        WHERE length(m.hex) >= 7 AND oe.bits = 28 AND oe.prefix = left(m.hex, 7)
        UNION ALL
        SELECT oe.country, oe.bits
        FROM oui_entries oe, m
        WHERE length(m.hex) >= 6 AND oe.bits = 24 AND oe.prefix = left(m.hex, 6)
    ) matches
    ORDER BY bits DESC
    LIMIT 1
$$;
