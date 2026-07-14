SELECT
    hwcomponent
  , class
  , slot
  , description
  , vendor
  , model
  , serial
  , firmware
  , status
  , is_fru
  , updated_at
FROM
    proj_hardware_inventory
WHERE
    device = $1
ORDER BY
    class       ASC NULLS LAST
  , slot        ASC NULLS LAST
  , hwcomponent ASC
