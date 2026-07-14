-- Five proj_* tables were added over time (CoAP, NBNS, mDNS/SSDP service, and Home
-- Assistant device-registry projections) without a corresponding retention_policies row,
-- so RetentionService has never pruned them — they grow unbounded. proj_discovered's
-- per-device detail tables get the same 7-day network-state policy as proj_discovered
-- itself; proj_service_ha_devices gets the same 90-day stable policy as proj_services
-- (both hold a service's current-state facts, not a time series).

INSERT INTO retention_policies
(
      table_name
    , category
    , stale_after
    , notes
)
VALUES
(
    'proj_discovered_coap_formats'
    , 'network'
    , INTERVAL '7 days'
    , 'CoAP content-format detail per discovered device; same lifetime as proj_discovered'
)
     ,
(
    'proj_discovered_coap_resources'
    , 'network'
    , INTERVAL '7 days'
    , 'CoAP resource detail per discovered device; same lifetime as proj_discovered'
)
     ,
(
    'proj_discovered_nbns_names'
    , 'network'
    , INTERVAL '7 days'
    , 'NetBIOS name-service detail per discovered device; same lifetime as proj_discovered'
)
     ,
(
    'proj_discovered_services'
    , 'network'
    , INTERVAL '7 days'
    , 'mDNS/SSDP service records per discovered device; same lifetime as proj_discovered'
)
    ON CONFLICT (TABLE_NAME) DO NOTHING;

INSERT INTO retention_policies
(
      table_name
    , category
    , stale_after
    , notes
)
VALUES
(
    'proj_service_ha_devices'
    , 'stable'
    , INTERVAL '90 days'
    , 'Home Assistant device-registry entries; current-state facts, same lifetime as proj_services'
)
    ON CONFLICT (TABLE_NAME) DO NOTHING;
