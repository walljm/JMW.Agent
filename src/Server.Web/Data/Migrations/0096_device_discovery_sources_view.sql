-- The "observed by" set per device: the identifying source stamped on each fingerprint, UNIONed
-- with passive observers derived from projection presence (ARP/DHCP tables + the scanner list in
-- proj_discovered.sources, mapped from scanner class name to the short slug shown in the UI).
-- Extracted from DeviceListApi.BaseCte so the devices list and the dashboard composition
-- "by discovery source" breakdown share one definition instead of duplicating the slug map.
-- (AgentDetailModel keeps its own per-agent scanner-stat map — different aggregation, not this.)
CREATE OR REPLACE VIEW device_discovery_sources AS
WITH mac_obs AS (
    SELECT mac AS m, 'arp'        AS source FROM proj_device_arp        WHERE mac   IS NOT NULL
    UNION ALL
    SELECT lease,     'dhcp'                FROM proj_dhcp_leases       WHERE lease IS NOT NULL
    UNION ALL
    SELECT lease,     'dhcp-local'          FROM proj_dhcp_local_leases WHERE lease IS NOT NULL
    UNION ALL
    -- One row per network scanner that actually touched this MAC; the comma-joined scanner class
    -- names in proj_discovered.sources are mapped to the same short slug shown elsewhere in the app
    -- (matches AgentDetailModel.ScannerStatNames). An unrecognized class falls back to its own
    -- lowercased form rather than being dropped.
    SELECT
        d.mac
      , COALESCE(names.slug, lower(tok)) AS source
    FROM
        proj_discovered d
        CROSS JOIN LATERAL unnest(string_to_array(d.sources, ',')) AS tok
        LEFT JOIN LATERAL (
            VALUES
                ('ArpScanner', 'arp')
              , ('MdnsScanner', 'mdns')
              , ('SsdpScanner', 'ssdp')
              , ('SnmpBroadcastScanner', 'snmp-broadcast')
              , ('GatewaySnmpArpScanner', 'gateway-arp')
              , ('NbnsScanner', 'nbns')
              , ('LlmnrScanner', 'llmnr')
              , ('WsDiscoveryScanner', 'ws-discovery')
              , ('DnsPtrScanner', 'dns-ptr')
              , ('HttpBannerScanner', 'http-banner')
              , ('TlsCertScanner', 'tls-cert')
              , ('Smb2Scanner', 'smb2')
              , ('SshBannerScanner', 'ssh-banner')
              , ('LdapScanner', 'ldap')
              , ('EurekaScanner', 'eureka')
              , ('IppScanner', 'ipp')
              , ('SnmpPrinterScanner', 'snmp-printer')
              , ('RokuScanner', 'roku')
              , ('AirPlayScanner', 'airplay')
              , ('PingSweepScanner', 'ping-sweep')
              , ('CoApScanner', 'coap')
              , ('RtspScanner', 'rtsp')
              , ('MqttScanner', 'mqtt')
              , ('PhilipsHueScanner', 'philips-hue')
              , ('OnvifScanner', 'onvif')
              , ('BacnetScanner', 'bacnet')
              , ('ModbusScanner', 'modbus')
        ) AS names (class_name, slug) ON names.class_name = tok
    WHERE
        d.mac IS NOT NULL
        AND tok <> ''
)
SELECT device_id, source FROM device_fingerprints WHERE source IS NOT NULL
UNION
SELECT
    df.device_id
  , mo.source
FROM
    device_fingerprints df
    JOIN mac_obs mo ON mo.m = df.fp_value
WHERE
    df.fp_type = 'mac';
