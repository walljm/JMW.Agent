-- Adds proj_discovered.snmp_serial (Printer-MIB prtGeneralSerialNumber, Device[].Discovered[].
-- SnmpSerial). The agent's SNMP scanner has emitted this fact since it was added, but it never
-- had a projection column, so DiscoveryMaterializer's serial-only promotion (GetNewDiscovered
-- SerialsAsync / GetNewDiscoveredMacsAsync) could never see it and it never reached
-- proj_hardware.system_serial (the hardware serial shown on a device's details page) — same
-- promotion path as onvif_serial/roku_serial (migration 0006).
ALTER TABLE jmwdiscovery.proj_discovered
    ADD COLUMN IF NOT EXISTS snmp_serial text;
