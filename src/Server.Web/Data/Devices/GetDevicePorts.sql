SELECT
    listeningport
  , protocol
  , address
  , port
  , process_name
  , pid
  , updated_at
FROM
    proj_ports
WHERE
    device = $1
ORDER BY
    port          ASC NULLS LAST
  , listeningport ASC
