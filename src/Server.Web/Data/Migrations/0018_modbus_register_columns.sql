-- The modbus register projections key on the HoldingRegister / InputRegister
-- dimensions. GenericProjection derives a dimension's column by lowercasing the
-- dimension name with no separator (HoldingRegister -> holdingregister), the same
-- convention proj_service_ca_dns_names.dnsname uses. Migration 0010 created these
-- columns as holding_register / input_register (with an underscore), so the writer
-- targeted a column that did not exist and the projections could never populate.
-- Rename the columns to the derived names. Idempotent: skipped if already renamed.

DO
$$
BEGIN
    IF
EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'jmwdiscovery'
          AND TABLE_NAME = 'proj_modbus_holding'
          AND COLUMN_NAME = 'holding_register'
    ) THEN
ALTER TABLE proj_modbus_holding RENAME COLUMN holding_register TO holdingregister;
END IF;

    IF
EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'jmwdiscovery'
          AND TABLE_NAME = 'proj_modbus_input'
          AND COLUMN_NAME = 'input_register'
    ) THEN
ALTER TABLE proj_modbus_input RENAME COLUMN input_register TO inputregister;
END IF;
END
$$;
