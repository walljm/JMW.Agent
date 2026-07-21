-- Scalar OUI vendor+country lookup for a single MAC (or a trustworthy 6-hex-nibble OUI
-- prefix salvaged from a masked/obscured MAC). Backs the device-detail "Interfaces" fact
-- view's OUI column — a render-time computed column, since OUI resolution lives in the
-- oui_vendor()/oui_country() Postgres functions, not in the fact stream itself.
SELECT
    oui_vendor($1)  AS vendor
  , oui_country($1) AS country
