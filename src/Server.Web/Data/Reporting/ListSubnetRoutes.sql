-- Connected/static IPv4 routes (excludes the default route, handled separately by
-- ListDefaultRoutes.sql). Fills subnet-membership gaps that single-valued
-- proj_interfaces.ipv4 can miss — an interface with a secondary address in another subnet
-- only keeps the last-written IP for that (device, interface) key, but every distinct route
-- destination survives independently.
SELECT
    device
  , route AS destination
FROM
    proj_device_routes
WHERE
    route <> '0.0.0.0/0'
  AND route ~ '^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}/[0-9]{1,2}$'
