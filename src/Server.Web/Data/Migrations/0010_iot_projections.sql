SET
search_path TO jmwdiscovery, PUBLIC;

-- BACnet device projection: one row per device, all Device object properties.
CREATE TABLE if NOT EXISTS proj_bacnet_device (
    device
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    device_instance
    BIGINT,
    vendor_name
    TEXT,
    vendor_id
    TEXT,
    model_name
    TEXT,
    object_name
    TEXT,
    firmware_revision
    TEXT,
    app_software_version
    TEXT,
    description
    TEXT,
    location
    TEXT,
    system_status
    TEXT,
    serial_number
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );

-- Modbus device identification from FC 43 / MEI Type 14: one row per device.
CREATE TABLE if NOT EXISTS proj_modbus_device (
    device
    TEXT
    NOT
    NULL
    PRIMARY
    KEY,
    vendor_name
    TEXT,
    product_code
    TEXT,
    revision
    TEXT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       )
    );

-- Modbus holding registers: one row per (device, register address).
CREATE TABLE if NOT EXISTS proj_modbus_holding (
    device
    TEXT
    NOT
    NULL,
    holding_register
    TEXT
    NOT
    NULL,
    VALUE
    BIGINT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 holding_register
                )
    );

-- Modbus input registers: one row per (device, register address).
CREATE TABLE if NOT EXISTS proj_modbus_input (
    device
    TEXT
    NOT
    NULL,
    input_register
    TEXT
    NOT
    NULL,
    VALUE
    BIGINT,
    updated_at
    TIMESTAMPTZ
    NOT
    NULL
    DEFAULT
    now(
       ),
    PRIMARY KEY (device,
                 input_register
                )
    );
