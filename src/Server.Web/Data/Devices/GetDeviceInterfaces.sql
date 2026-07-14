SELECT
    interface
  , name
  , mac_address
  , obscured_mac
  -- Obscured MACs (Google Wifi/OnHub firmware) preserve only the real OUI (first 6 hex
  -- nibbles); the rest is fabricated. When there's no reconstructed real MAC, fall back to
  -- just that trustworthy prefix (same idiom as HostsApi.QueryAsync / InterfacesApi.QueryAsync).
  , COALESCE(
        oui_vendor(mac_address),
        oui_vendor(left(regexp_replace(lower(obscured_mac), '[^0-9a-f]', '', 'g'), 6))
    ) AS oui
  , COALESCE(
        oui_country(mac_address),
        oui_country(left(regexp_replace(lower(obscured_mac), '[^0-9a-f]', '', 'g'), 6))
    ) AS oui_country
  , ipv4
  , ipv6
  , mtu
  , up
  , speed_bps
  , duplex
  , type
  , updated_at
FROM
    proj_interfaces
WHERE
    device = $1
ORDER BY
    name      ASC NULLS LAST
  , interface ASC
