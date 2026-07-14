-- Add mac_address column to proj_interfaces.
-- The MAC address is collected by the agent's NetworkCollector as a fact
-- (FactPaths.InterfaceMAC) but was not included in the projection definition.
ALTER TABLE jmwdiscovery.proj_interfaces
    ADD COLUMN if NOT EXISTS mac_address text;
