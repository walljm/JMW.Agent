-- Device[].Neighbor[] facts (LLDP-derived L2 adjacency — see docs/plans/d3-l2-l3.md), pivoted
-- from the raw fact log into one row per (device, neighbor). This subtree has no dedicated
-- projection table (view-only, see FactViewLibrary "Neighbors"), so L2TopologyApi reads the
-- latest value per fact id directly, same DISTINCT ON idiom as GetDeviceAllFacts.sql.
WITH latest AS (
    SELECT
        DISTINCT
    ON (id)
        id
      , attribute_path
      , key_values
      , COALESCE (value_str, value_long::text, value_double::text) AS VALUE
    FROM
        facts_history
    WHERE
        attribute_path LIKE 'Device[].Neighbor[].%'
    ORDER BY
        id
      , collected_at DESC
)
SELECT
    l.key_values ->> 'Device'   AS device
  , l.key_values ->> 'Neighbor' AS neighbor_key
  , COALESCE(s.friendly_name, s.hostname) AS hostname
  , MAX (CASE WHEN l.attribute_path = 'Device[].Neighbor[].LocalPort' THEN l.value END)        AS local_port
  , MAX (CASE WHEN l.attribute_path = 'Device[].Neighbor[].RemoteChassisId' THEN l.value END)   AS remote_chassis_id
  , MAX (CASE WHEN l.attribute_path = 'Device[].Neighbor[].RemotePortId' THEN l.value END)      AS remote_port_id
  , MAX (CASE WHEN l.attribute_path = 'Device[].Neighbor[].RemoteSysName' THEN l.value END)     AS remote_sys_name
  , MAX (CASE WHEN l.attribute_path = 'Device[].Neighbor[].RemoteMac' THEN l.value END)          AS remote_mac
  , MAX (CASE WHEN l.attribute_path = 'Device[].Neighbor[].RemoteIp' THEN l.value END)           AS remote_ip
  , MAX (CASE WHEN l.attribute_path = 'Device[].Neighbor[].Protocol' THEN l.value END)           AS protocol
FROM
    latest                l
    LEFT JOIN proj_systems s
    ON s.device = l.key_values ->> 'Device'
GROUP BY
    l.key_values ->> 'Device'
  , l.key_values ->> 'Neighbor'
  , s.friendly_name
  , s.hostname
