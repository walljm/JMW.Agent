-- Device-vendor unification pass. Every protocol collector's vendor fact (hardware/BACnet/
-- Modbus/Google Wifi) now fans into one canonical field via DeviceVendorDerivation, landing in
-- proj_devices.vendor — no more COALESCE-across-N-tables at every read site, and no more
-- silently-missing sources (proj_devices.vendor itself was already missing from that COALESCE
-- before this pass).
--
-- With vendor_name redirected to the derivation, proj_bacnet_device and proj_modbus_device have
-- no column left with any cross-device query need (confirmed: nothing but the single-device
-- Device Detail tabs ever read them). Dropped entirely — all fields now surfaced via the
-- "BACnet Details" / "Modbus Details" / "Modbus Registers" fact views (FactViewLibrary.cs).
DROP TABLE proj_bacnet_device;
DROP TABLE proj_modbus_device;
DROP TABLE proj_modbus_holding;
DROP TABLE proj_modbus_input;
